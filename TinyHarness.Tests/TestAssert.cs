using System.Diagnostics.CodeAnalysis;

namespace TinyHarness.Tests;

/// <summary>
///     <para>提供需要返回值或保留调用方 null 流分析的断言辅助方法。</para>
///     <para>Supplies assertion helpers that return values or preserve the caller's null-flow analysis.</para>
/// </summary>
internal static class TestAssert
{
    /// <summary>
    ///     <para>验证序列恰好含有一个元素并返回该元素。</para>
    ///     <para>Verifies that a sequence contains exactly one item and returns it.</para>
    /// </summary>
    /// <typeparam name="T">
    ///     <para>序列元素类型。</para>
    ///     <para>The sequence item type.</para>
    /// </typeparam>
    public static T Single<T>(IEnumerable<T> collection)
    {
        var values = collection.Take(2).ToArray();
        Assert.That(values, Has.Length.EqualTo(1));
        return values[0];
    }

    /// <summary>
    ///     <para>验证序列恰好含有一个满足条件的元素并返回该元素。</para>
    ///     <para>Verifies that exactly one sequence item matches and returns it.</para>
    /// </summary>
    /// <typeparam name="T">
    ///     <para>序列元素类型。</para>
    ///     <para>The sequence item type.</para>
    /// </typeparam>
    public static T Single<T>(IEnumerable<T> collection, Predicate<T> predicate)
    {
        var values = collection.Where(item => predicate(item)).Take(2).ToArray();
        Assert.That(values, Has.Length.EqualTo(1));
        return values[0];
    }

    /// <summary>
    ///     <para>断言字符串包含指定片段并遵循给定的比较规则。</para>
    ///     <para>Asserts that a string contains a substring using the specified comparison.</para>
    /// </summary>
    public static void Contains(string expected, string? actual, StringComparison comparison)
    {
        Assert.That(actual is not null && actual.Contains(expected, comparison), Is.True);
    }

    /// <summary>
    ///     <para>断言字符串不包含指定片段并遵循给定的比较规则。</para>
    ///     <para>Asserts that a string does not contain a substring using the specified comparison.</para>
    /// </summary>
    public static void DoesNotContain(string expected, string? actual, StringComparison comparison)
    {
        Assert.That(actual is null || !actual.Contains(expected, comparison), Is.True);
    }

    /// <summary>
    ///     <para>断言序列中至少有一个元素满足谓词。</para>
    ///     <para>Asserts that at least one sequence item matches the predicate.</para>
    /// </summary>
    public static void ContainsAll<T>(Predicate<T> predicate, IEnumerable<T> collection)
    {
        Assert.That(collection.Any(item => predicate(item)), Is.True);
    }

    /// <summary>
    ///     <para>断言序列中没有元素满足谓词。</para>
    ///     <para>Asserts that no sequence item matches the predicate.</para>
    /// </summary>
    public static void DoesNotContainAny<T>(Predicate<T> predicate, IEnumerable<T> collection)
    {
        Assert.That(collection.Any(item => predicate(item)), Is.False);
    }

    /// <summary>
    ///     <para>验证同步委托抛出指定异常并返回异常实例。</para>
    ///     <para>Verifies that a synchronous delegate throws the specified exception and returns it.</para>
    /// </summary>
    public static T Throws<T>(Action code) where T : Exception => Assert.Throws<T>(() => code())!;

    /// <summary>
    ///     <para>验证异步委托抛出指定异常并返回异常实例。</para>
    ///     <para>Verifies that an asynchronous delegate throws the specified exception and returns it.</para>
    /// </summary>
    public static async Task<T> ThrowsAsync<T>(Func<Task> code) where T : Exception =>
        (await Assert.ThrowsAsync<T>(async () => await code()))!;

    /// <summary>
    ///     <para>验证对象具有指定的精确运行时类型并返回该对象。</para>
    ///     <para>Verifies that an object has the specified exact runtime type and returns it.</para>
    /// </summary>
    /// <typeparam name="T">
    ///     <para>预期类型。</para>
    ///     <para>The expected type.</para>
    /// </typeparam>
    public static T TypeOf<T>(object? actual)
    {
        Assert.That(actual, Is.TypeOf<T>());
        return (T)actual!;
    }

    /// <summary>
    ///     <para>验证序列中的每个元素均满足断言。</para>
    ///     <para>Verifies the assertion for every item in a sequence.</para>
    /// </summary>
    /// <typeparam name="T">
    ///     <para>序列元素类型。</para>
    ///     <para>The sequence item type.</para>
    /// </typeparam>
    public static void All<T>(IEnumerable<T> collection, Action<T> assertion)
    {
        Assert.Multiple(() =>
        {
            foreach (var item in collection)
                assertion(item);
        });
    }

    /// <summary>
    ///     <para>验证对象非 null 并返回该对象。</para>
    ///     <para>Verifies that an object is non-null and returns it.</para>
    /// </summary>
    /// <typeparam name="T">
    ///     <para>对象类型。</para>
    ///     <para>The object type.</para>
    /// </typeparam>
    public static T NotNull<T>([NotNull] T? actual)
    {
        actual = actual ?? throw new AssertionException("Expected a non-null value.");
        return actual;
    }
}
