using Avalonia.Media;
using TinyHarness.Desktop.Models;
using System.Text.Json;

namespace TinyHarness.Desktop.Utils;

/// <summary>
///     工具调用折叠行文案（对齐官方 DSH 客户端规则）。协议不携带描述字段，
///     口语化标题与摘要由客户端本地推导：已知工具名映射中文标题，摘要按工具
///     变体从参数 JSON 取首个命中字段的首行；未知名回退通用标题并在摘要前
///     保留原始名。
/// </summary>
public static class ToolCallText
{
    // 行式条目图标（复刻官方 ui-primitives 16×16 线稿、线宽 1）：描边与填充拆成
    // 两个几何——混合图标（读取/编辑的实底框、思考的中心点）由 XAML 双 Path 叠加。
    private const string ApiStroke    = "M3,4 L7,8 L3,12 M9,12 L13,12";
    private const string BrowseStroke = "M4.9375,5.90295 L11.0625,5.90295 M4.9375,9.02991 L8.27841,9.02991";

    private const string BrowseFill =
        "M12.5,1.32617 C13.3039,1.32617 14,1.95171 14,2.77637 L14,13.2246 C13.9996,14.0489 13.3036,14.6738 12.5,14.6738 L3.5,14.6738 C2.69637,14.6738 2.00042,14.0489 2,13.2246 L2,2.77637 C2,1.95171 2.69613,1.32617 3.5,1.32617 L12.5,1.32617 Z M3.5,2.32617 C3.1993,2.32617 3,2.55186 3,2.77637 L3,13.2246 C3.00044,13.4489 3.19963,13.6738 3.5,13.6738 L12.5,13.6738 C12.8004,13.6738 12.9996,13.4489 13,13.2246 L13,2.77637 C13,2.55186 12.8007,2.32617 12.5,2.32617 L3.5,2.32617 Z";

    private const string EditStroke = "M7.7849,8.23878 L13.888,2.13574";

    private const string EditFill =
        "M8.85596,2.69971 L4.19971,2.69971 C3.37141,2.69971 2.69992,3.37146 2.69971,4.19971 L2.69971,11.8003 C2.69992,12.6285 3.37141,13.3003 4.19971,13.3003 L11.8003,13.3003 C12.6283,13.2999 13.3001,12.6283 13.3003,11.8003 L13.3003,7.89893 L14.3003,7.89893 L14.3003,11.8003 C14.3001,13.1806 13.1806,14.2999 11.8003,14.3003 L4.19971,14.3003 C2.81913,14.3003 1.69992,13.1808 1.69971,11.8003 L1.69971,4.19971 C1.69992,2.81918 2.81913,1.69971 4.19971,1.69971 L8.85596,1.69971 Z";

    private const string CodeStroke =
        "M6.27612,1.5 L4.52612,14.5 M11.4739,1.5 L9.72388,14.5 M2.39868,5.5 L14.0681,5.5 M1.93188,10.5 L13.6013,10.5";

    private const string SearchStroke =
        "M6.58727,11.8586 C9.55061,11.8586 11.9529,9.45637 11.9529,6.49304 C11.9529,3.5297 9.55061,1.12744 6.58727,1.12744 C3.62394,1.12744 1.22168,3.5297 1.22168,6.49304 C1.22168,9.45637 3.62394,11.8586 6.58727,11.8586 Z M10.2991,10.3933 L14.7783,14.8725";

    private const string ChecklistStroke =
        "M3.75,6.25 C4.7165,6.25 5.5,5.4665 5.5,4.5 C5.5,3.5335 4.7165,2.75 3.75,2.75 C2.7835,2.75 2,3.5335 2,4.5 C2,5.4665 2.7835,6.25 3.75,6.25 Z M7.5,4.5 L13.5,4.5 M3.75,13.25 C4.7165,13.25 5.5,12.4665 5.5,11.5 C5.5,10.5335 4.7165,9.75 3.75,9.75 C2.7835,9.75 2,10.5335 2,11.5 C2,12.4665 2.7835,13.25 3.75,13.25 Z M7.5,11.5 L13.5,11.5";

    private const string DeliverStroke =
        "M6.15479,4.91687 L9.84543,4.91687 M11.8798,9.55347 L11.8798,2.71525 C11.8798,2.37416 11.564,2.09766 11.1744,2.09766 L4.82577,2.09766 C4.43618,2.09766 4.12036,2.37416 4.12036,2.71525 L4.12036,9.55347 M2.28735,13.8022 L2.28735,8.84792 C2.28735,8.77514 2.36262,8.72673 2.42884,8.75693 L13.2936,13.7112 C13.3914,13.7558 13.3596,13.9022 13.2521,13.9022 L2.38735,13.9022 C2.33213,13.9022 2.28735,13.8575 2.28735,13.8022 Z M7.46929,10.979 L13.5783,8.7416 C13.6435,8.7177 13.7126,8.76601 13.7126,8.83551 L13.7125,13.8022 C13.7125,13.8574 13.6678,13.9022 13.6125,13.9022 L7.99999,13.9022 M6.15479,7.2395 L9.05644,7.2395";

    private const string SparkleStroke =
        "M5.875,3 C5.875,6.33333 7.54167,8 10.875,8 C7.54167,8 5.875,9.66667 5.875,13 C5.875,9.66667 4.20833,8 0.875,8 C4.20833,8 5.875,6.33333 5.875,3 Z M12.375,1.55823 C12.375,3.39156 13.2917,4.30823 15.125,4.30823 C13.2917,4.30823 12.375,5.22489 12.375,7.05823 C12.375,5.22489 11.4583,4.30823 9.625,4.30823 C11.4583,4.30823 12.375,3.39156 12.375,1.55823 Z M12.375,10.4418 C12.375,11.7751 13.0417,12.4418 14.375,12.4418 C13.0417,12.4418 12.375,13.1084 12.375,14.4418 C12.375,13.1084 11.7083,12.4418 10.375,12.4418 C11.7083,12.4418 12.375,11.7751 12.375,10.4418 Z";

    private const string ThinkStroke =
        "M10.2854,5.71481 C12.9673,8.39663 14.1182,11.5938 12.8562,12.8559 C11.5942,14.1179 8.39706,12.9669 5.71518,10.2851 C3.03333,7.60323 1.88236,4.40608 3.14441,3.14403 C4.40644,1.882 7.6036,3.03297 10.2854,5.71481 Z M10.2854,10.2851 C7.6036,12.9669 4.40644,14.1179 3.14441,12.8559 C1.88236,11.5938 3.03333,8.39663 5.71518,5.71481 C8.39706,3.03297 11.5942,1.882 12.8562,3.14403 C14.1182,4.40608 12.9673,7.60323 10.2854,10.2851 Z";

    private const string ThinkFill =
        "M8.86291,8.0002 C8.86291,8.47549 8.47762,8.86087 8.00224,8.86087 C7.52694,8.86087 7.1416,8.47549 7.1416,8.0002 C7.1416,7.52485 7.52694,7.13953 8.00224,7.13953 C8.47762,7.13953 8.86291,7.52485 8.86291,8.0002 Z";

    // 官方 keyed toolview 行专属图标（web-row / ask-question-row / details-row /
    // CordisRunRow / CordisActionRow）。官方 <circle> 元素按两段 180° 圆弧机械转换
    // （端点取 cx±r）；StreamGeometry 不携带端帽样式，赤道线等端帽差异见各自注释。

    private const string GlobeStroke =
        "M7.99986,14.0887 C11.3626,14.0887 14.0886,11.3627 14.0886,7.99998 C14.0886,4.63727 11.3626,1.91125 7.99986,1.91125 C4.63715,1.91125 1.91113,4.63727 1.91113,7.99998 C1.91113,11.3627 4.63715,14.0887 7.99986,14.0887 Z M2.34619,8 L13.6538,8 M7.99976,14.0889 C9.23509,14.0889 10.1743,11.3629 10.1743,8.00006 C10.1743,4.63739 9.23509,1.91138 7.99976,1.91138 M7.99973,14.0889 C6.76445,14.0889 5.8252,11.3629 5.8252,8.00006 C5.8252,4.63739 6.76445,1.91138 7.99973,1.91138";

    private const string QuestionStroke =
        "M8,14.5 C11.5899,14.5 14.5,11.5899 14.5,8 C14.5,4.41015 11.5899,1.5 8,1.5 C4.41015,1.5 1.5,4.41015 1.5,8 C1.5,11.5899 4.41015,14.5 8,14.5 Z M5.75,6.69646 C5.75,6.29865 5.88196,5.90976 6.12919,5.57899 C6.37643,5.24821 6.72783,4.99041 7.13896,4.83817 C7.5501,4.68593 8.0025,4.6461 8.43895,4.72371 C8.87541,4.80132 9.27632,4.99289 9.59099,5.27419 C9.90566,5.55549 10.12,5.91388 10.2068,6.30406 C10.2936,6.69423 10.249,7.09866 10.0787,7.4662 C9.90843,7.83373 9.62004,8.14787 9.25003,8.36889 C9.19476,8.4019 9.13803,8.43262 9.08004,8.46099 C8.52566,8.73217 8,9.20817 8,9.82532 M8,10.7416 L8,11.7416";

    private const string AgentFill =
        "M6.51867,12.3282 C7.29816,12.6011 8.16475,12.6514 9.02269,12.4216 C9.57879,12.2726 10.0784,12.0185 10.5087,11.6888 C10.7819,12.0555 11.1606,12.3304 11.5913,12.4805 C10.9688,13.029 10.2149,13.4478 9.35911,13.6771 C8.13946,14.0038 6.90632,13.8971 5.82126,13.4533 C6.15821,13.1562 6.4021,12.7652 6.51867,12.3282 Z M9.17629,2.89409 C11.1101,3.34433 12.739,4.81872 13.2889,6.87043 C13.4219,7.3665 13.4811,7.8649 13.4774,8.35466 C13.0924,8.13213 12.6422,8.01837 12.1741,8.05276 L12.1711,8.05257 C12.1539,7.77199 12.109,7.48889 12.0334,7.20684 C11.6363,5.72533 10.5048,4.6372 9.13549,4.22844 C9.25559,3.87667 9.29214,3.49087 9.22309,3.09892 C9.2108,3.02922 9.19451,2.96108 9.17629,2.89409 Z M4.7311,3.89107 L4.78302,4.11879 C4.87648,4.4488 5.04146,4.74263 5.25579,4.98896 C3.98078,6.01355 3.35848,7.72904 3.8089,9.41059 C3.81828,9.44559 3.82866,9.48025 3.83885,9.51479 C3.38217,9.61268 2.98548,9.84137 2.68107,10.1556 C2.63414,10.022 2.5897,9.88632 2.55244,9.74726 C1.93301,7.43489 2.86717,5.07173 4.71504,3.76697 L4.7311,3.89107 Z";

    private const string AgentStroke =
        "M7.99136,5.28105 C8.87501,5.28105 9.59136,4.56471 9.59136,3.68105 C9.59136,2.7974 8.87501,2.08105 7.99136,2.08105 C7.1077,2.08105 6.39136,2.7974 6.39136,3.68105 C6.39136,4.56471 7.1077,5.28105 7.99136,5.28105 Z M3.94009,12.9417 C4.82374,12.9417 5.54009,12.2254 5.54009,11.3417 C5.54009,10.458 4.82374,9.7417 3.94009,9.7417 C3.05643,9.7417 2.34009,10.458 2.34009,11.3417 C2.34009,12.2254 3.05643,12.9417 3.94009,12.9417 Z M12.0851,12.9417 C12.9688,12.9417 13.6851,12.2254 13.6851,11.3417 C13.6851,10.458 12.9688,9.7417 12.0851,9.7417 C11.2015,9.7417 10.4851,10.458 10.4851,11.3417 C10.4851,12.2254 11.2015,12.9417 12.0851,12.9417 Z";

    private const string UsersStroke =
        "M6,8.25 C7.51878,8.25 8.75,7.01878 8.75,5.5 C8.75,3.98122 7.51878,2.75 6,2.75 C4.48122,2.75 3.25,3.98122 3.25,5.5 C3.25,7.01878 4.48122,8.25 6,8.25 Z M1,14.5 C1,11.5 3.5,10.25 6,10.25 C8.5,10.25 11,11.5 11,14.5 M10.5,2.9 C11.65,3.35 12.45,4.35 12.45,5.5 C12.45,6.65 11.65,7.65 10.5,8.1 M12.4,10.6 C13.9,11.3 15,12.6 15,14.5";

    private const string GoalStroke =
        "M14.5001,8 C14.5,9.28552 14.1188,10.5422 13.4045,11.611 C12.6903,12.6799 11.6752,13.5129 10.4875,14.0049 C9.29982,14.4968 7.99295,14.6255 6.73212,14.3747 C5.4713,14.124 4.31314,13.505 3.4041,12.596 C2.49514,11.687 1.87614,10.5288 1.62537,9.26798 C1.37459,8.00716 1.50331,6.70028 1.99525,5.51261 C2.48719,4.32494 3.32025,3.30981 4.3891,2.59557 C5.45795,1.88134 6.71458,1.50008 8.0001,1.5 M11.5,8 C11.5001,8.69227 11.2948,9.36901 10.9102,9.94463 C10.5257,10.5202 9.97901,10.9689 9.33944,11.2338 C8.69986,11.4987 7.99609,11.5681 7.31712,11.433 C6.63816,11.2979 6.01449,10.9645 5.52501,10.475 C5.03548,9.98552 4.70209,9.36185 4.56702,8.68289 C4.43195,8.00392 4.50127,7.30015 4.76619,6.66057 C5.03112,6.021 5.47976,5.47436 6.05538,5.08978 C6.631,4.70519 7.30774,4.49995 8.00001,4.5 M8.00024,7.99976 L11.2,4.80005 M12.4719,5.62245 C12.4246,5.66972 12.3569,5.69025 12.2913,5.67715 L10.7814,5.37555 C10.7022,5.35972 10.6402,5.29781 10.6244,5.2186 L10.3228,3.70866 C10.3097,3.6431 10.3302,3.57533 10.3775,3.52806 L12.1826,1.723 C12.2863,1.61929 12.4627,1.65879 12.5122,1.79684 L12.9271,2.95225 C12.9472,3.00847 12.9915,3.05272 13.0477,3.07291 L14.2031,3.48774 C14.3412,3.5373 14.3807,3.71368 14.277,3.81739 L12.4719,5.62245 Z";

    private const string ClockStroke =
        "M8,14 C11.3137,14 14,11.3137 14,8 C14,4.68629 11.3137,2 8,2 C4.68629,2 2,4.68629 2,8 C2,11.3137 4.68629,14 8,14 Z M8,4.31 L8,8.46 L11,10.08";

    private const string CordisFill =
        "M3.16143,6.59068 L1.75205,8.00006 L3.10619,9.35419 L2.39908,10.0613 L0.832948,8.49517 C0.559581,8.2218 0.559582,7.77831 0.832948,7.50494 L2.45432,5.88357 L3.16143,6.59068 Z M8.49511,15.1671 C8.22176,15.4405 7.77826,15.4404 7.50489,15.1671 L5.93461,13.5968 L6.64172,12.8897 L8,14.248 L9.40938,12.8386 L10.1165,13.5457 L8.49511,15.1671 Z M15.1671,7.50494 C15.4403,7.7782 15.4401,8.22179 15.1671,8.49517 L13.652,10.0102 L12.9449,9.30309 L14.248,8.00006 L12.8897,6.64178 L13.5968,5.93467 L15.1671,7.50494 Z M9.35414,3.10624 L8,1.7521 L6.69696,3.05514 L5.98986,2.34803 L7.50489,0.833003 C7.77828,0.559981 8.22186,0.559752 8.49511,0.833003 L10.0612,2.39913 L9.35414,3.10624 Z";

    private const string CordisStroke =
        "M6.23779,8 A1.76221,1.76221 0 1 0 9.76221,8 A1.76221,1.76221 0 1 0 6.23779,8 Z";

    private const string BranchStroke =
        "M1.01503,8.0001 L5.6964,8.0001 C6.41913,8.0001 6.78049,8.0001 7.12115,7.91951 C7.4232,7.84804 7.71233,7.73014 7.97821,7.57 C8.27809,7.38939 8.5364,7.13669 9.05303,6.63129 L11.3281,4.40564 M1.01221,7.9999 L5.6964,7.9999 C6.41913,7.9999 6.78049,7.9999 7.12115,8.08049 C7.4232,8.15196 7.71233,8.26986 7.97821,8.43 C8.27809,8.61061 8.5364,8.86331 9.05303,9.36871 L11.3281,11.5944 M10.88058,3.3079 A1.56962,1.56962 0 1 0 14.01982,3.3079 A1.56962,1.56962 0 1 0 10.88058,3.3079 Z M10.88058,12.6921 A1.56962,1.56962 0 1 0 14.01982,12.6921 A1.56962,1.56962 0 1 0 10.88058,12.6921 Z";

    // 停止图标（官方 IconStopFillRegular 为纯填充几何）：行模型要求描边部件非空，
    // 以同一几何叠加描边与填充近似官方实底方块。
    private const string StopFill =
        "M12.5,2.5 L3.5,2.5 C2.94772,2.5 2.5,2.94772 2.5,3.5 L2.5,12.5 C2.5,13.0523 2.94772,13.5 3.5,13.5 L12.5,13.5 C13.0523,13.5 13.5,13.0523 13.5,12.5 L13.5,3.5 C13.5,2.94772 13.0523,2.5 12.5,2.5 Z";

    private const string TrashStroke =
        "M1.28149,3.88831 L14.7187,3.88831 M5.41602,3.88833 L5.41602,2.47962 C5.41602,2.29282 5.52492,2.11366 5.71876,1.98157 C5.9126,1.84948 6.17551,1.77527 6.44964,1.77527 L9.55053,1.77527 C9.82466,1.77527 10.0876,1.84948 10.2814,1.98157 C10.4753,2.11366 10.5842,2.29282 10.5842,2.47962 L10.5842,3.88833 M2.57349,3.88831 L3.19366,13.2943 C3.21937,13.5502 3.33952,13.7872 3.53065,13.9593 C3.72178,14.1313 3.97016,14.2259 4.22729,14.2246 L11.7728,14.2246 C12.0299,14.2259 12.2783,14.1313 12.4694,13.9593 C12.6605,13.7872 12.7807,13.5502 12.8064,13.2943 L13.4266,3.88831 M6.44946,6.98926 L6.44946,11.1238 M9.55054,6.98926 L9.55054,11.1238";

    /// <summary>工具名 → 摘要变体（对齐官方 TOOL_VARIANTS；缺席即 others 变体）。</summary>
    private static readonly Dictionary<string, string> Variants = new()
    {
        ["shell"]                  = "bash",
        ["read_file"]              = "read",
        ["list_files"]             = "read",
        ["search_text"]            = "search",
        ["apply_patch"]            = "edit",
        ["work_items"]             = "todo",
        ["bash"]                   = "bash",
        ["pwsh"]                   = "bash",
        ["read"]                   = "read",
        ["read_image"]             = "read",
        ["web_fetch"]              = "read",
        ["cordis_package_inspect"] = "read",
        ["cordis_runtime_inspect"] = "read",
        ["web_search"]             = "search",
        ["grep"]                   = "search",
        ["glob"]                   = "search",
        ["write"]                  = "write",
        ["edit"]                   = "edit",
        ["run_code"]               = "code",
        ["present"]                = "present",
        ["todo_write"]             = "todo"
    };

    /// <summary>变体 → 摘要字段优先级（对齐官方 SUMMARY_KEYS）。</summary>
    private static readonly Dictionary<string, string[]> SummaryKeys = new()
    {
        ["bash"]   = ["description", "command"],
        ["read"]   = ["path", "file_path", "url"],
        ["search"] = ["query", "pattern", "url"],
        ["write"]  = ["path", "file_path"],
        ["edit"]   = ["path", "file_path"],
        ["code"]   = ["description"]
    };

    /// <summary>
    ///     工具名 → 行图标覆盖：官方 keyed toolview 行的专属图标（details-row 的
    ///     detailIcon、web-row 的 globe、ask-question-row 的 question、Cordis 卡片）。
    ///     缺席时仍按摘要变体查表，不改动 Variants 语义。
    /// </summary>
    private static readonly Dictionary<string, string> IconOverrides = new()
    {
        ["web_search"]           = "globe",
        ["ask_user_question"]    = "question",
        ["subagent"]             = "agent",
        ["list_subagent_models"] = "agent",
        ["list_agents"]          = "users",
        ["send_message"]         = "users",
        ["interrupt_agent"]      = "users",
        ["wait_agent"]           = "users",
        ["spawn_teammate"]       = "users",
        ["team_task_create"]     = "checklist",
        ["team_task_get"]        = "checklist",
        ["team_task_update"]     = "checklist",
        ["team_task_list"]       = "checklist",
        ["create_goal"]          = "goal",
        ["get_goal"]             = "goal",
        ["update_goal"]          = "goal",
        ["schedule_create"]      = "clock",
        ["schedule_list"]        = "clock",
        ["schedule_update"]      = "clock",
        ["schedule_delete"]      = "clock",
        ["job_list"]             = "checklist",
        ["job_output"]           = "checklist",
        ["job_kill"]             = "checklist",
        ["terminal_open"]        = "code",
        ["terminal_read"]        = "code",
        ["terminal_list"]        = "code",
        ["terminal_signal"]      = "code",
        ["terminal_close"]       = "code",
        ["lsp"]                  = "search",
        ["workflow"]             = "branch",
        ["ralph"]                = "branch",
        ["session_event_read"]   = "search",
        ["session_event_search"] = "search",
        ["session_event_trace"]  = "search",
        ["session_search"]       = "search",
        ["session_trace"]        = "search",
        ["cordis_inspect_list"]  = "cordis",
        ["cordis_inspect_query"] = "cordis",
        ["cordis_inspect_self"]  = "cordis",
        ["cordis_run"]           = "code",
        ["cordis_stop"]          = "stop",
        ["cordis_undefine"]      = "trash"
    };

    /// <summary>变体 → 行图标（stroke 必有，fill 仅实底部件非空）。</summary>
    private static readonly Dictionary<string, (StreamGeometry Stroke, StreamGeometry? Fill)> IconParts = new()
    {
        ["bash"]      = (Parse(ApiStroke), null),
        ["read"]      = (Parse(BrowseStroke), Parse(BrowseFill)),
        ["search"]    = (Parse(SearchStroke), null),
        ["write"]     = (Parse(EditStroke), Parse(EditFill)),
        ["edit"]      = (Parse(EditStroke), Parse(EditFill)),
        ["code"]      = (Parse(CodeStroke), null),
        ["present"]   = (Parse(DeliverStroke), null),
        ["todo"]      = (Parse(ChecklistStroke), null),
        ["checklist"] = (Parse(ChecklistStroke), null),
        ["agent"]     = (Parse(AgentStroke), Parse(AgentFill)),
        ["branch"]    = (Parse(BranchStroke), null),
        ["clock"]     = (Parse(ClockStroke), null),
        ["cordis"]    = (Parse(CordisStroke), Parse(CordisFill)),
        ["globe"]     = (Parse(GlobeStroke), null),
        ["goal"]      = (Parse(GoalStroke), null),
        ["question"]  = (Parse(QuestionStroke), null),
        ["stop"]      = (Parse(StopFill), Parse(StopFill)),
        ["trash"]     = (Parse(TrashStroke), null),
        ["users"]     = (Parse(UsersStroke), null),
        ["others"]    = (Parse(SparkleStroke), null)
    };

    /// <summary>思考行图标（ReasoningRow 的 IconThinkOutlineRegular）。</summary>
    public static readonly StreamGeometry ThinkingIconStroke = Parse(ThinkStroke);

    public static readonly StreamGeometry ThinkingIconFill = Parse(ThinkFill);

    /// <summary>折叠行图标描边部件；先查官方专属覆盖，再按变体，未知工具回退通用四角星。</summary>
    public static StreamGeometry GetIconStroke(string? name) => IconParts.GetValueOrDefault(IconKey(name)).Stroke;

    /// <summary>折叠行图标填充部件；纯描边图标为 null。</summary>
    public static StreamGeometry? GetIconFill(string? name) => IconParts.GetValueOrDefault(IconKey(name)).Fill;

    /// <summary>图标键：官方专属覆盖优先，缺席回落摘要变体（不影响摘要逻辑）。</summary>
    private static string IconKey(string? name) =>
        name != null && IconOverrides.TryGetValue(name, out var icon) ? icon : VariantKey(name);

    private static string VariantKey(string? name) =>
        name != null && Variants.TryGetValue(name, out var variant) ? variant : "others";

    private static StreamGeometry Parse(string data) => StreamGeometry.Parse(data);

    /// <summary>折叠行标题；未知或空白名回退通用「工具调用」。</summary>
    public static string GetTitle(string? name) => ToolCallLabels.GetTitle(name);

    /// <summary>
    ///     折叠行摘要：已知工具按变体取参数字段，未知名在摘要前保留原始名
    ///     （对齐官方 generic 行「名称 · 摘要」的口径）。
    /// </summary>
    public static string GetSummary(string? name, string? argumentsJson)
    {
        var summary = DeriveSummary(name, argumentsJson);
        if (string.IsNullOrWhiteSpace(name) || ToolCallLabels.IsKnown(name)) return summary;

        return string.IsNullOrEmpty(summary) ? name : $"{name} · {summary}";
    }

    /// <summary>状态词：present 工具对齐官方交付文案，其余用通用词。</summary>
    public static string GetStatusText(string? name, ToolActivityStatus status)
        => ToolCallLabels.GetStatusText(name, status);

    /// <summary>取首行（对齐官方 firstLine）：摘要与失败摘要都只展示一行。</summary>
    public static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline];
    }

    private static string DeriveSummary(string? name, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return string.Empty;

        using var document = TryParse(argumentsJson);
        if (document == null) return FirstLine(argumentsJson);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return FirstLine(argumentsJson);

        if (name == "present")
        {
            var files = ExtractPresentFiles(root);
            if (files.Length > 0) return string.Join(", ", files);
        }

        var variant = name != null && Variants.TryGetValue(name, out var mapped) ? mapped : "others";

        switch (variant)
        {
            case "todo" :
            {
                var todoSummary = TodoHeadSummary(root);
                if (todoSummary != null) return todoSummary;
                break;
            }
            case "search"
                when root.TryGetProperty("queries", out var queries) && queries.ValueKind == JsonValueKind.Array :
            {
                var list = new List<string>();
                foreach (var query in queries.EnumerateArray())
                    if (query.ValueKind == JsonValueKind.String
                     && query.GetString() is { Length: > 0 } text)
                        list.Add(FirstLine(text));

                if (list.Count > 0) return string.Join(", ", list);
                break;
            }
        }

        foreach (var key in SummaryKeys.GetValueOrDefault(variant, []))
            if (root.TryGetProperty(key, out var value)
             && value.ValueKind == JsonValueKind.String
             && value.GetString() is { Length: > 0 } picked)
                return FirstLine(picked);

        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String
             && property.Value.GetString() is { Length: > 0 } fallback)
                return FirstLine(fallback);

        return FirstLine(argumentsJson);
    }

    private static string[] ExtractPresentFiles(JsonElement args)
    {
        if (!args.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return [];

        var paths = new List<string>();
        foreach (var file in files.EnumerateArray())
            if (file.ValueKind == JsonValueKind.Object    &&
                file.TryGetProperty("path", out var path) &&
                path.ValueKind == JsonValueKind.String    &&
                path.GetString() is { Length: > 0 } text)
                paths.Add(FirstLine(text));

        return [.. paths];
    }

    /// <summary>
    ///     todo_write 参数摘要头段（对齐官方 todo-row summarize + planSummary）：
    ///     「{done}/{total} 已完成」，存在进行中条目时以「 · 」追加其内容首行；
    ///     参数无 todos 数组返回 null 走通用摘要兜底（保持原始 JSON 兜底口径）。
    /// </summary>
    private static string? TodoHeadSummary(JsonElement args)
    {
        if (!args.TryGetProperty("todos", out var array) || array.ValueKind != JsonValueKind.Array) return null;

        var items = ParseTodoItems(array);
        var done  = items.Count(item => item.Status == SessionTodoStatus.Completed);
        var head  = $"{done}/{items.Count} 已完成";
        var active = items.FirstOrDefault(item => item.Status == SessionTodoStatus.InProgress &&
                                                  !string.IsNullOrWhiteSpace(item.Content));
        return active is null ? head : $"{head} · {FirstLine(active.Content)}";
    }

    /// <summary>
    ///     从 todo_write 参数 JSON 解析任务清单（与 WireEventJson.TryGetTodos 同一
    ///     条目规则：content 须为字符串，status 未知值回退 pending）；无 todos 数组
    ///     或 JSON 不可解析返回 null。
    /// </summary>
    public static IReadOnlyList<SessionTodoItem>? ParseTodos(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;

        using var document = TryParse(argumentsJson);
        if (document == null) return null;

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object       ||
            !root.TryGetProperty("todos", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return null;

        return ParseTodoItems(array);
    }

    private static List<SessionTodoItem> ParseTodoItems(JsonElement array)
    {
        var items = new List<SessionTodoItem>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            var content = element.TryGetProperty("content", out var contentElement) &&
                          contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;
            if (content is null) continue;

            var status = element.TryGetProperty("status", out var statusElement) &&
                         statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
            items.Add(new SessionTodoItem(content, status switch
            {
                "completed"   => SessionTodoStatus.Completed,
                "in_progress" => SessionTodoStatus.InProgress,
                _             => SessionTodoStatus.Pending
            }));
        }

        return items;
    }

    /// <summary>
    ///     官方 todoDiffModel 的摘要行：当前清单与上次写入清单按 content 匹配，
    ///     状态变化或位置移动都计「更新」；「新增 n · 更新 n · 移除 n」中计数为零的
    ///     段省略，全零为「清单没有变化」；baseline 为 null（尚无 todo/write 事件）
    ///     返回 null，摘要行不含 diff 段（对齐官方首次记录行为）。
    /// </summary>
    public static string? TodoDiffSummary(
        IReadOnlyList<SessionTodoItem>? baseline, IReadOnlyList<SessionTodoItem> current)
    {
        if (baseline is null) return null;

        var previous = new Dictionary<string, (SessionTodoStatus Status, int Index)>();
        for (var index = 0; index < baseline.Count; index++)
            previous[baseline[index].Content] = (baseline[index].Status, index);

        var carried = new HashSet<string>();
        var added   = 0;
        var updated = 0;
        for (var index = 0; index < current.Count; index++)
        {
            var item = current[index];
            if (!previous.TryGetValue(item.Content, out var before))
            {
                added++;
                continue;
            }

            carried.Add(item.Content);
            if (before.Status != item.Status || before.Index != index) updated++;
        }

        var removed = previous.Keys.Count(content => !carried.Contains(content));
        if (added == 0 && updated == 0 && removed == 0) return "清单没有变化";

        var parts = new List<string>(3);
        if (added   > 0) parts.Add($"新增 {added}");
        if (updated > 0) parts.Add($"更新 {updated}");
        if (removed > 0) parts.Add($"移除 {removed}");
        return string.Join(" · ", parts);
    }

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
