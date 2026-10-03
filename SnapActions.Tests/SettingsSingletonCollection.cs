using Xunit;

namespace SnapActions.Tests;

/// <summary>
///     Settings 是进程级单例（<see cref="Config.SettingsManager.Current"/>）：有测试类会临时改它
///     （加/删用户动作、改 AppHiddenActions、保存/重载），也有测试类通过 <c>new ActionRegistry()</c>
///     读取它。并行跑时"读"会撞上"写"，出现随机的 id 缺失/数量不符（历史上表现为
///     ActionRegistryTests.GetAllKnownActionIds_IncludesEveryActionInRegistry 偶发失败）。
///     放进这个禁用并行化的集合后，这些类彼此以及与其他集合都不再并发，测试结果才可复现。
/// </summary>
[CollectionDefinition("settings-singleton", DisableParallelization = true)]
public class SettingsSingletonCollection;
