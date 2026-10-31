using System.Net;
using System.Net.Http;
using System.Text;
using NUnit.Framework;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Services;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class ModelDiscoveryTests
{
    [TestCase("https://provider.invalid/v1", "https://provider.invalid/v1/models")]
    [TestCase("https://provider.invalid/custom/api/", "https://provider.invalid/custom/api/models")]
    [TestCase("http://localhost:8080", "http://localhost:8080/models")]
    public async Task RequestsModelListWithBearerAndPreservesBasePath(string endpoint, string expected)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo(expected));
            Assert.That(request.Headers.Authorization!.ToString(), Is.EqualTo("Bearer offline-key"));
            return Task.FromResult(Json("""
                                        {"data":[{"id":"z-model"},{"id":"a-model"},{"id":"a-model"},
                                                 {"id":""},{"id":"bad id"},{"id":12},{"name":"ignored"},null]}
                                        """));
        }));
        var models = await ProviderModelDiscovery.FetchAsync(client, endpoint, "offline-key", CancellationToken.None);
        Assert.That(models, Is.EqualTo(new[] { "a-model", "z-model" }));
    }

    [TestCase("")]
    [TestCase("file:///tmp/models")]
    [TestCase("https://user:password@provider.invalid/v1")]
    [TestCase("https://provider.invalid/v1?key=secret")]
    [TestCase("https://provider.invalid/v1#fragment")]
    public void RejectsInvalidBaseUrls(string endpoint) =>
        Assert.Throws<InvalidDataException>(() => ProviderModelDiscovery.ModelsUri(endpoint));

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.Redirect)]
    public async Task HttpErrorsDoNotIncludeServerSecrets(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("server echoed offline-key")
        })));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                                                                           await ProviderModelDiscovery
                                                                              .FetchAsync(client,
                                                                                   "https://provider.invalid/v1",
                                                                                   "offline-key",
                                                                                   CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain(((int)status).ToString()));
        Assert.That(exception.Message, Does.Not.Contain("offline-key"));
    }

    [TestCase("{}")]
    [TestCase("{\"data\":{}}")]
    [TestCase("[]")]
    public async Task RejectsIncompatibleModelLists(string json)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(json))));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
                                                           await ProviderModelDiscovery.FetchAsync(client,
                                                               "https://provider.invalid/v1", null,
                                                               CancellationToken.None));
    }

    [Test]
    public async Task RejectsOversizedResponses()
    {
        using var client =
            new HttpClient(new Handler((_, _) => Task.FromResult(Json(new string('x', 4 * 1024 * 1024 + 1)))));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
                                                           await ProviderModelDiscovery.FetchAsync(client,
                                                               "https://provider.invalid/v1", null,
                                                               CancellationToken.None));
    }

    [Test]
    public async Task CancellationReachesHttpTransport()
    {
        using var cancellation = new CancellationTokenSource();
        var       entered      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json("{\"data\":[]}");
        }));
        var fetch = ProviderModelDiscovery.FetchAsync(client, "https://provider.invalid/v1", null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () => await fetch);
    }

    [TestCase(ChatApiKind.ChatCompletions)]
    [TestCase(ChatApiKind.Responses)]
    public async Task SelectedModelsAppendToDraftAndSaveWithoutChangingExistingModels(ChatApiKind protocol)
    {
        var credentials = new Credentials();
        var (editor, path, baseline) = CreateEditor(credentials, protocol);
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        Assert.That(credentials.ReadCount, Is.Zero);
        editor.FetchModels = (_, key, _) =>
        {
            Assert.That(key, Is.EqualTo("stored-offline-key"));
            return Task.FromResult<IReadOnlyList<string>>(["existing", "new-a", "new-b"]);
        };
        await editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.IsModelPickerOpen, Is.True);
        var existing = editor.FilteredDiscoveredModels.Single(model => model.Id == "existing");
        Assert.That(existing.IsAlreadyAdded, Is.True);
        Assert.That(existing.IsSelected, Is.True);
        existing.IsSelected = false;
        Assert.That(editor.ModelsToRemoveCount, Is.EqualTo(1));
        existing.IsSelected = true;
        Assert.That(editor.ModelsToRemoveCount, Is.Zero);
        var listReplacements = 0;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(editor.FilteredDiscoveredModels)) listReplacements++;
        };
        editor.FilteredDiscoveredModels.Single(model => model.Id == "new-a").IsSelected = true;
        Assert.That(listReplacements, Is.Zero,
                    "Checking a row must not replace the list and reset scrolling or focus.");
        editor.ModelSearch = "NEW-B";
        Assert.That(editor.FilteredDiscoveredModels.Select(model => model.Id), Is.EqualTo(new[] { "new-b" }));
        Assert.That(editor.ModelsToAddCount, Is.EqualTo(1));
        Assert.That(credentials.SaveCount, Is.Zero);
        Assert.That((await UserConfigStore.LoadAsync(path, CancellationToken.None)).Profiles[0].Models.Count,
                    Is.EqualTo(1));

        editor.SaveModelSelectionCommand.Execute(null);
        Assert.That(editor.IsModelPickerOpen, Is.False);
        Assert.That(editor.ModelEntries.Select(model => model.Id), Is.EqualTo(new[] { "existing", "new-a" }));
        Assert.That(editor.DefaultModelOptions.Any(option => option.Value == "new-a"), Is.True);
        Assert.That(editor.CanSave, Is.True);
        await editor.SaveAsync();
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles[0].Models[0], Is.EqualTo(baseline.Profiles[0].Models[0]));
        Assert.That(saved.Profiles[0].Models[1].ContextWindowTokens,
                    Is.EqualTo(new TinyHarnessConfig().ContextWindowTokens));
        Assert.That(saved.Profiles[0].Models[1].ReasoningEfforts, Is.Null);
        Assert.That(saved.Profiles[0].ChatApi, Is.EqualTo(protocol));
        Assert.That(saved.Profiles[0].DefaultModel, Is.EqualTo("existing"));
        Assert.That(credentials.ReadCount, Is.EqualTo(1));
        Assert.That(credentials.SaveCount, Is.Zero);
    }

    [Test]
    public async Task NewKeyIsUsedWithoutWritingCredentialsAndRepeatedFetchSkipsAddedDrafts()
    {
        var credentials = new Credentials();
        var (editor, _, _) = CreateEditor(credentials);
        editor.Secret      = "new-offline-key";
        editor.FetchModels = (_, key, _) =>
        {
            Assert.That(key, Is.EqualTo("new-offline-key"));
            return Task.FromResult<IReadOnlyList<string>>(["new-a"]);
        };
        await editor.FetchModelsCommand.ExecuteAsync();
        editor.FilteredDiscoveredModels.Single().IsSelected = true;
        editor.SaveModelSelectionCommand.Execute(null);
        await editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.FilteredDiscoveredModels.Single().IsAlreadyAdded, Is.True);
        Assert.That(editor.CanSaveModelSelection, Is.False);
        Assert.That(editor.ModelEntries.Count, Is.EqualTo(2));
        Assert.That(credentials.ReadCount, Is.Zero);
        Assert.That(credentials.SaveCount, Is.Zero);
        Assert.That(editor.Secret, Is.EqualTo("new-offline-key"));
    }

    [Test]
    public async Task EnvironmentCredentialIsUsedOnlyOnExplicitFetch()
    {
        var variable = "TINYHARNESS_DISCOVERY_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "environment-offline-key");
        try
        {
            var credentials = new Credentials();
            var (editor, _, _)         = CreateEditor(credentials);
            editor.CredentialTarget    = string.Empty;
            editor.EnvironmentVariable = variable;
            editor.FetchModels = (_, key, _) =>
            {
                Assert.That(key, Is.EqualTo("environment-offline-key"));
                return Task.FromResult<IReadOnlyList<string>>([]);
            };
            await editor.FetchModelsCommand.ExecuteAsync();
            Assert.That(editor.ModelDiscoveryStatus, Does.Contain("未返回"));
            Assert.That(editor.ModelEntries.Count, Is.EqualTo(1));
            Assert.That(credentials.ReadCount, Is.Zero);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ClosingOrEditingConnectionCancelsAndDiscardsLateResults(bool changeEndpoint)
    {
        var (editor, _, _) = CreateEditor(new Credentials());
        var completion =
            new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        editor.FetchModels = (_, _, token) =>
        {
            requestToken = token;
            return completion.Task;
        };
        var fetch = editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.IsFetchingModels, Is.True);
        if (changeEndpoint) editor.Endpoint = "https://changed.invalid/v1";
        else editor.CloseModelPickerCommand.Execute(null);
        Assert.That(requestToken.IsCancellationRequested, Is.True);
        completion.SetResult(["late-result"]);
        await fetch;
        Assert.That(editor.IsModelPickerOpen, Is.False);
        Assert.That(editor.FilteredDiscoveredModels, Is.Empty);
        Assert.That(editor.ModelEntries.Count, Is.EqualTo(1));
        Assert.That(editor.IsFetchingModels, Is.False);
    }

    [Test]
    public async Task CredentialFailureDoesNotSendRequestOrExposeBackendError()
    {
        var (editor, _, _) = CreateEditor(new Credentials { FailRead = true });
        editor.FetchModels = (_, _, _) => throw new AssertionException("Must not request models without credentials.");
        await editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.ModelDiscoveryStatus, Does.Contain("无法读取"));
        Assert.That(editor.ModelDiscoveryStatus, Does.Not.Contain("backend-secret"));
        Assert.That(editor.ModelEntries.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task DiscoveryWorksBeforeProviderIsSaved()
    {
        var credentials = new Credentials();
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-new-provider-" + Guid.NewGuid().ToString("N"),
                                "config.json");
        var editor = CatalogProviderEditorViewModel.ForCreate(path, credentials, new UserConfig(), _ => { }, _ => { });
        editor.Name        = "new-provider";
        editor.Endpoint    = "https://provider.invalid/v1";
        editor.Secret      = "new-offline-key";
        editor.FetchModels = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["new-model"]);
        await editor.FetchModelsCommand.ExecuteAsync();
        editor.FilteredDiscoveredModels.Single().IsSelected = true;
        editor.SaveModelSelectionCommand.Execute(null);
        Assert.That(File.Exists(path), Is.False);
        Assert.That(credentials.SaveCount, Is.Zero);
        await editor.SaveAsync();
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles.Single().Models.Single().Id, Is.EqualTo("new-model"));
        Assert.That(credentials.SaveCount, Is.EqualTo(1));
    }

    [TestCase("close")]
    [TestCase("section")]
    [TestCase("card")]
    public async Task LeavingProviderCancelsDiscovery(string action)
    {
        var credentials = new Credentials();
        var (_, path, baseline) = CreateEditor(credentials);
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var catalog = new CatalogSettingsViewModel(path, () => Task.CompletedTask, credentials);
        var general = new GeneralSettingsSectionViewModel(path);
        var panel   = new SettingsPanelViewModel(general, catalog);
        await panel.OpenAsync();
        panel.SelectSection(catalog);
        catalog.Providers[0].EditCommand.Execute(null);
        var editor = catalog.Editor!;
        var completion =
            new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        editor.FetchModels = (_, _, token) =>
        {
            requestToken = token;
            return completion.Task;
        };
        var fetch = editor.FetchModelsCommand.ExecuteAsync();
        switch (action)
        {
            case "close" :   panel.CloseCommand.Execute(null); break;
            case "section" : panel.SelectSection(general); break;
            case "card" :    catalog.OpenAddCommand.Execute(null); break;
        }

        Assert.That(requestToken.IsCancellationRequested, Is.True);
        completion.SetResult(["late-model"]);
        await fetch;
        Assert.That(editor.FilteredDiscoveredModels, Is.Empty);
    }

    [Test]
    public async Task SelectAllUsesCurrentFilterAndPreservesOtherSelections()
    {
        var (editor, _, _) = CreateEditor(new Credentials());
        editor.FetchModels = (_, _, _) =>
            Task.FromResult<IReadOnlyList<string>>(["existing", "glm-a", "glm-b", "other"]);
        await editor.FetchModelsCommand.ExecuteAsync();
        editor.FilteredDiscoveredModels.Single(model => model.Id == "other").IsSelected = true;
        editor.ModelSearch                                                              = "glm";
        editor.SelectAllModelsCommand.Execute(null);
        Assert.That(editor.ModelsToAddCount, Is.EqualTo(3));
        Assert.That(editor.SelectAllModelsText, Is.EqualTo("取消全选"));
        editor.SelectAllModelsCommand.Execute(null);
        Assert.That(editor.ModelsToAddCount, Is.EqualTo(1));
        editor.ModelSearch = string.Empty;
        Assert.That(editor.FilteredDiscoveredModels.Single(model => model.Id == "other").IsSelected, Is.True);
        Assert.That(editor.FilteredDiscoveredModels.Single(model => model.Id == "existing").IsSelected, Is.True);
        editor.ModelSearch = "no-match";
        Assert.That(editor.HasFilteredModels, Is.False);
        editor.SelectAllModelsCommand.Execute(null);
        Assert.That(editor.ModelsToAddCount, Is.EqualTo(1));
    }

    [Test]
    public async Task DismissClosesModelDialogBeforeSettingsAndRetainsProviderDraft()
    {
        var credentials = new Credentials();
        var (_, path, baseline) = CreateEditor(credentials);
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var catalog = new CatalogSettingsViewModel(path, () => Task.CompletedTask, credentials);
        var panel   = new SettingsPanelViewModel(new GeneralSettingsSectionViewModel(path), catalog);
        await panel.OpenAsync();
        panel.SelectSection(catalog);
        catalog.Providers[0].EditCommand.Execute(null);
        var editor = catalog.Editor!;
        editor.Endpoint    = "https://edited.invalid/v1";
        editor.FetchModels = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["new-model"]);
        var notifications = 0;
        catalog.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(catalog.IsModelPickerOpen)) notifications++;
        };
        var closed = 0;
        panel.CloseRequested += (_, _) => closed++;
        await editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(catalog.IsModelPickerOpen, Is.True);
        panel.DismissCommand.Execute(null);
        Assert.That(catalog.IsModelPickerOpen, Is.False);
        Assert.That(closed, Is.Zero);
        Assert.That(catalog.Editor, Is.SameAs(editor));
        Assert.That(editor.Endpoint, Is.EqualTo("https://edited.invalid/v1"));
        Assert.That(notifications, Is.EqualTo(2));
        panel.DismissCommand.Execute(null);
        Assert.That(closed, Is.EqualTo(1));
    }

    [Test]
    public async Task LoadingDotsCycleAndResetOnRetryWhileExistingModelsStayUnchanged()
    {
        var (editor, _, _) = CreateEditor(new Credentials());
        var completion =
            new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.FetchModels = (_, _, _) => completion.Task;
        var fetch = editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.ModelListMessage, Is.EqualTo("正在获取模型."));
        foreach (var expected in new[] { "正在获取模型..", "正在获取模型...", "正在获取模型." })
        {
            editor.AdvanceLoadingIndicator();
            Assert.That(editor.ModelListMessage, Is.EqualTo(expected));
        }

        completion.SetResult(["new-model"]);
        await fetch;
        Assert.That(editor.HasFilteredModels, Is.True);
        Assert.That(editor.IsFetchingModels, Is.False);
        editor.AdvanceLoadingIndicator();
        Assert.That(editor.ModelListMessage, Is.EqualTo("暂无可选结果"));
        Assert.That(editor.ModelEntries.Single().Id, Is.EqualTo("existing"));

        completion =
            new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fetch = editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.ModelListMessage, Is.EqualTo("正在获取模型."));
        editor.AdvanceLoadingIndicator();
        editor.CloseModelPickerCommand.Execute(null);
        editor.AdvanceLoadingIndicator();
        Assert.That(editor.ModelListMessage, Is.EqualTo("正在获取模型.."));
        completion.SetCanceled();
        await fetch;
        Assert.That(editor.IsFetchingModels, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LoadingStopsForEmptyAndFailedResults(bool fail)
    {
        var (editor, _, _) = CreateEditor(new Credentials());
        editor.FetchModels = (_, _, _) => fail
            ? Task.FromException<IReadOnlyList<string>>(new HttpRequestException("offline failure"))
            : Task.FromResult<IReadOnlyList<string>>([]);
        await editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.IsFetchingModels, Is.False);
        Assert.That(editor.HasFilteredModels, Is.False);
        editor.AdvanceLoadingIndicator();
        Assert.That(editor.ModelListMessage, Is.EqualTo("暂无可选结果"));
        if (fail) Assert.That(editor.ModelDiscoveryStatus, Does.Contain("无法连接"));
    }

    [Test]
    public async Task SelectionAddsAndRemovesOnlyConfirmedModelsAndPreservesUnreturnedModels()
    {
        var credentials = new Credentials();
        var (_, path, original) = CreateEditor(credentials);
        var retained = new UserProfileModel
        {
            Id               = "not-returned", DisplayName = "Keep me", ContextWindowTokens = 32000,
            ReasoningEfforts = [ModelReasoningEffort.High], DefaultReasoningEffort = ModelReasoningEffort.High
        };
        var profile = original.Profiles[0] with { Models = [original.Profiles[0].Models[0], retained] };
        var other   = new UserProfile { Name = "other", Endpoint = "https://other.invalid/v1" };
        var baseline = original with
        {
            Profiles = [profile, other], Settings = new UserConfigSettings { MaxAgentSteps = 17 }
        };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var editor = CatalogProviderEditorViewModel.ForEdit(path, credentials, baseline, profile, _ => { }, _ => { });
        editor.FetchModels = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["existing", "new-model"]);
        await editor.FetchModelsCommand.ExecuteAsync();
        editor.FilteredDiscoveredModels.Single(model => model.Id == "existing").IsSelected  = false;
        editor.FilteredDiscoveredModels.Single(model => model.Id == "new-model").IsSelected = true;
        editor.ModelSearch                                                                  = "new-model";
        Assert.That(editor.ModelSelectionSummary, Is.EqualTo("新增 1 个，移除 1 个"));
        Assert.That(editor.ModelEntries.Select(entry => entry.Id), Is.EqualTo(new[] { "existing", "not-returned" }));
        editor.SaveModelSelectionCommand.Execute(null);
        Assert.That(editor.ModelEntries.Select(entry => entry.Id), Is.EqualTo(new[] { "not-returned", "new-model" }));
        Assert.That(editor.DefaultModelLabel, Is.EqualTo("无默认"));
        Assert.That(editor.DefaultModelOptions.Any(option => option.Value == "existing"), Is.False);
        Assert.That((await UserConfigStore.LoadAsync(path, CancellationToken.None)).Profiles[0].Models.Count,
                    Is.EqualTo(2));
        Assert.That(editor.CanSave, Is.True);
        await editor.SaveAsync();
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles[0].Models.Select(model => model.Id),
                    Is.EqualTo(new[] { "not-returned", "new-model" }));
        Assert.That(saved.Profiles[0].Models[0].DisplayName, Is.EqualTo(retained.DisplayName));
        Assert.That(saved.Profiles[0].Models[0].ContextWindowTokens, Is.EqualTo(retained.ContextWindowTokens));
        Assert.That(saved.Profiles[0].Models[0].ReasoningEfforts, Is.EqualTo(retained.ReasoningEfforts));
        Assert.That(saved.Profiles[0].Models[0].DefaultReasoningEffort, Is.EqualTo(retained.DefaultReasoningEffort));
        Assert.That(saved.Profiles[0].DefaultModel, Is.Empty);
        Assert.That(saved.Profiles[0].ApiKeyCredentialTarget, Is.EqualTo(profile.ApiKeyCredentialTarget));
        Assert.That(saved.DefaultProfile, Is.EqualTo(baseline.DefaultProfile));
        Assert.That(saved.Profiles[1].Models, Is.Empty);
        Assert.That(saved.Profiles[1] with { Models = other.Models }, Is.EqualTo(other));
        Assert.That(saved.Settings, Is.EqualTo(baseline.Settings));
        Assert.That(credentials.SaveCount, Is.Zero);
    }

    [Test]
    public async Task CancelledUncheckDoesNotModifyProviderDraftOrStoredCatalog()
    {
        var (editor, path, baseline) = CreateEditor(new Credentials());
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var before = await File.ReadAllTextAsync(path);
        editor.FetchModels = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["existing"]);
        await editor.FetchModelsCommand.ExecuteAsync();
        editor.FilteredDiscoveredModels.Single().IsSelected = false;
        Assert.That(editor.CanSaveModelSelection, Is.True);
        editor.CloseModelPickerCommand.Execute(null);
        Assert.That(editor.IsDirty, Is.False);
        Assert.That(editor.ModelEntries.Single().Id, Is.EqualTo("existing"));
        Assert.That(editor.DefaultModelLabel, Is.EqualTo("existing"));
        await editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.FilteredDiscoveredModels.Single().IsSelected, Is.True);
        Assert.That(editor.ModelsToRemoveCount, Is.Zero);
        Assert.That(editor.CanSaveModelSelection, Is.False);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RemovingNonDefaultOrAllModelsIsDirtyAndPersists(bool removeAll)
    {
        var credentials = new Credentials();
        var (_, path, original) = CreateEditor(credentials);
        var profile = original.Profiles[0] with
        {
            Models =
            [
                original.Profiles[0].Models[0], new UserProfileModel { Id = "second", ContextWindowTokens = 64000 }
            ]
        };
        var baseline = original with { Profiles = [profile] };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var editor = CatalogProviderEditorViewModel.ForEdit(path, credentials, baseline, profile, _ => { }, _ => { });
        editor.FetchModels = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["existing", "second"]);
        await editor.FetchModelsCommand.ExecuteAsync();
        if (removeAll) editor.SelectAllModelsCommand.Execute(null);
        else editor.FilteredDiscoveredModels.Single(model => model.Id == "second").IsSelected = false;
        Assert.That(editor.ModelsToRemoveCount, Is.EqualTo(removeAll ? 2 : 1));
        Assert.That(editor.ModelsToAddCount, Is.Zero);
        editor.SaveModelSelectionCommand.Execute(null);
        Assert.That(editor.IsDirty, Is.True);
        Assert.That(editor.CanSave, Is.True);
        await editor.SaveAsync();
        var saved = (await UserConfigStore.LoadAsync(path, CancellationToken.None)).Profiles.Single();
        Assert.That(saved.Models.Count, Is.EqualTo(removeAll ? 0 : 1));
        Assert.That(saved.DefaultModel, Is.EqualTo(removeAll ? string.Empty : "existing"));
    }

    [Test]
    public async Task ReaddingRemovedDraftRestoresItsEditedParameters()
    {
        var (editor, path, baseline) = CreateEditor(new Credentials());
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var entry = editor.ModelEntries.Single();
        entry.DisplayName  = "Unsaved name";
        entry.WindowDraft  = "96K";
        editor.FetchModels = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["existing"]);
        await editor.FetchModelsCommand.ExecuteAsync();
        editor.FilteredDiscoveredModels.Single().IsSelected = false;
        editor.SaveModelSelectionCommand.Execute(null);
        Assert.That(editor.ModelEntries, Is.Empty);
        await editor.FetchModelsCommand.ExecuteAsync();
        editor.FilteredDiscoveredModels.Single().IsSelected = true;
        editor.SaveModelSelectionCommand.Execute(null);
        Assert.That(editor.ModelEntries.Single(), Is.SameAs(entry));
        Assert.That(entry.DisplayName, Is.EqualTo("Unsaved name"));
        Assert.That(entry.WindowDraft, Is.EqualTo("96K"));
        await editor.SaveAsync();
        var saved = (await UserConfigStore.LoadAsync(path, CancellationToken.None)).Profiles.Single().Models.Single();
        Assert.That(saved.DisplayName, Is.EqualTo("Unsaved name"));
        Assert.That(saved.ContextWindowTokens, Is.EqualTo(96000));
    }

    [Test]
    public async Task ConcurrentCatalogEditRejectsRemovalAndRetainsTheDraft()
    {
        var (editor, path, baseline) = CreateEditor(new Credentials());
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        editor.FetchModels = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["existing"]);
        await editor.FetchModelsCommand.ExecuteAsync();
        editor.FilteredDiscoveredModels.Single().IsSelected = false;
        editor.SaveModelSelectionCommand.Execute(null);
        var concurrent = ModelCatalogEditor.PutModel(baseline, "test",
                                                     new UserProfileModel
                                                         { Id = "external", ContextWindowTokens = 32000 }, false);
        await UserConfigStore.SaveAsync(path, concurrent, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => editor.SaveAsync());
        Assert.That(editor.ModelEntries, Is.Empty);
        Assert.That(editor.CanSave, Is.True);
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles[0].Models.Select(model => model.Id), Is.EqualTo(new[] { "existing", "external" }));
    }

    [Test]
    public async Task FailedFetchCanRetryAndPreservesDraft()
    {
        var (editor, _, _) = CreateEditor(new Credentials());
        editor.FetchModels = (_, _, _) => throw new HttpRequestException("do not show stored-offline-key");
        await editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.ModelDiscoveryStatus, Does.Not.Contain("stored-offline-key"));
        Assert.That(editor.ModelEntries.Count, Is.EqualTo(1));
        editor.FetchModels = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["retry-model"]);
        await editor.FetchModelsCommand.ExecuteAsync();
        Assert.That(editor.FilteredDiscoveredModels.Single().Id, Is.EqualTo("retry-model"));
    }

    private static (CatalogProviderEditorViewModel Editor, string Path, UserConfig Baseline) CreateEditor(
        Credentials credentials, ChatApiKind protocol = ChatApiKind.ChatCompletions)
    {
        var profile = new UserProfile
        {
            Name = "test", Endpoint = "https://provider.invalid/v1", ChatApi = protocol,
            ApiKeyCredentialTarget = "offline-target", DefaultModel = "existing",
            Models = [new UserProfileModel { Id = "existing", DisplayName = "Keep name", ContextWindowTokens = 64000 }]
        };
        var baseline = new UserConfig { DefaultProfile = profile.Name, Profiles = [profile] };
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinyharness-discovery-" + Guid.NewGuid().ToString("N"),
                                           "config.json");
        return (CatalogProviderEditorViewModel.ForEdit(path, credentials, baseline, profile, _ => { }, _ => { }),
                path, baseline);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken  cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool IsSupported => true;
        public bool FailRead    { get; init; }
        public int  ReadCount   { get; private set; }
        public int  SaveCount   { get; private set; }

        public string? Read(string targetName)
        {
            ReadCount++;
            if (FailRead) throw new IOException("backend-secret");
            return "stored-offline-key";
        }

        public void Save(string   targetName, string secret) => SaveCount++;
        public bool Delete(string targetName) => throw new AssertionException("Discovery must not delete credentials.");
    }
}
