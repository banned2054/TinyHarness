using System.Diagnostics;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Core.Services.Agent;

public sealed partial class AgentLoop
{
    /// <summary>
    ///     <para>为一个逻辑请求累计有界观测；只读取 SDK 事件，不推断传输或服务端内部阶段。</para>
    ///     <para>
    ///         Accumulates bounded observations for one logical request from SDK events without inferring transport or
    ///         server internals.
    ///     </para>
    /// </summary>
    private sealed class ModelRequestObservation(
        int requestNumber, bool summaryRequest, int estimatedInputTokens, int messageCount, StreamAccumulator response)
    {
        private readonly Stopwatch         _elapsed = Stopwatch.StartNew();
        private          long?             _firstStreamEvent;
        private          long?             _firstReasoning;
        private          long?             _firstText;
        private          long?             _lastStreamEvent;
        private          ModelRequestPhase _phase;
        private          int               _publishedPhases;

        /// <summary>
        ///     <para>记录流活动并指示是否首次观察到该种类；结束事件不会伪造首个 SDK 响应。</para>
        ///     <para>
        ///         Records stream activity and indicates its first observation; synthetic end events do not invent an SDK
        ///         response.
        ///     </para>
        /// </summary>
        internal bool Observe(ChatStreamEvent value)
        {
            if (value.Kind == ChatStreamEventKind.End) return false;

            var now = _elapsed.ElapsedMilliseconds;
            _firstStreamEvent ??= now;
            _lastStreamEvent  =   now;
            switch (value.Kind)
            {
                case ChatStreamEventKind.ReasoningDelta when !string.IsNullOrEmpty(value.ReasoningDelta) :
                case ChatStreamEventKind.ReasoningItem :
                    _firstReasoning ??= now;
                    _phase          =   ModelRequestPhase.ReceivingReasoning;
                    break;
                case ChatStreamEventKind.ContentDelta when value.ContentDelta.Length > 0 :
                    _firstText ??= now;
                    _phase     =   ModelRequestPhase.ReceivingText;
                    break;
                case ChatStreamEventKind.ToolCallDelta :
                    _phase = ModelRequestPhase.ReceivingTools;
                    break;
                default :
                    if (_phase == ModelRequestPhase.WaitingForResponse) _phase = ModelRequestPhase.ReceivingResponse;
                    break;
            }

            var phaseBit = 1 << (int)_phase;
            if ((_publishedPhases & phaseBit) != 0) return false;
            _publishedPhases |= phaseBit;
            return true;
        }

        /// <summary>
        ///     <para>生成可展示和落盘的观测快照，不包含正文、推理文本、opaque 或请求头。</para>
        ///     <para>Creates a displayable and persistable observation snapshot without text, reasoning, opaque data or headers.</para>
        /// </summary>
        internal ModelRequestProgress Snapshot(string? outcome = null, bool partialText = false) =>
            new(requestNumber, summaryRequest, _elapsed.ElapsedMilliseconds, outcome, partialText)
            {
                Phase                        = _phase,
                EstimatedInputTokens         = estimatedInputTokens,
                InputMessageCount            = messageCount,
                FirstStreamEventMilliseconds = _firstStreamEvent,
                FirstReasoningMilliseconds   = _firstReasoning,
                FirstTextMilliseconds        = _firstText,
                LastStreamEventMilliseconds  = _lastStreamEvent,
                ReasoningCharacters          = response.ReasoningCharacters,
                TextCharacters               = response.TextCharacters,
                InputTokens                  = response.InputTokens,
                OutputTokens                 = response.OutputTokens,
                CachedInputTokens            = response.CachedInputTokens,
                ReasoningTokens              = response.ReasoningTokens
            };
    }
}
