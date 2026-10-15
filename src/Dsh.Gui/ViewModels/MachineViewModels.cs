using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Fleet;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/** 机器列表的一行: 机器清单里的记录 + 最近一次得知的连接状态、host 版本与平台(来自常驻连接或"测试连接")。 */
public sealed partial class MachineRowViewModel(MachineSpec spec, bool isImplicit) : ObservableObject
{
    public MachineSpec Spec => spec;

    public string Name => spec.Name;

    public string KindText => spec.IsLocal ? "本机" : "SSH";

    public string Target => spec.Target;

    /** 隐式的本机(local): 不在 machines.json 里, 不能编辑或删除。 */
    public bool IsImplicit => isImplicit;

    [ObservableProperty]
    private string _stateText = "未连接";

    [ObservableProperty]
    private string? _hostVersion;

    [ObservableProperty]
    private string? _platform;

    [ObservableProperty]
    private string? _detail;

    public void Apply(MachineConnectionState state, HostInfo? info, string? detail)
    {
        StateText = StateLabel(state);
        if (info is not null)
            (HostVersion, Platform) = (info.HostVersion, info.Platform);
        Detail = detail;
    }

    /** 重新载入清单后保留同名机器最近一次得知的状态。 */
    internal void CopyStatusFrom(MachineRowViewModel other)
        => (StateText, HostVersion, Platform, Detail) = (other.StateText, other.HostVersion, other.Platform, other.Detail);

    public static string StateLabel(MachineConnectionState state) => state switch
    {
        MachineConnectionState.Connected => "已连接",
        MachineConnectionState.Connecting => "连接中",
        MachineConnectionState.Reconnecting => "重连中",
        MachineConnectionState.Deploying => "部署中",
        _ => "离线",
    };
}

/** 诊断结果的一行; Fix 是可直接复制运行的修复命令。 */
public sealed class DoctorCheckRow(DoctorCheck check)
{
    public string Name => check.Name;

    public DoctorOutcome Outcome => check.Outcome;

    public string OutcomeText => check.Outcome switch
    {
        DoctorOutcome.Ok => "正常",
        DoctorOutcome.Warning => "警告",
        DoctorOutcome.Failed => "失败",
        _ => "跳过",
    };

    public bool IsOk => check.Outcome == DoctorOutcome.Ok;

    public bool IsWarning => check.Outcome == DoctorOutcome.Warning;

    public bool IsFailed => check.Outcome == DoctorOutcome.Failed;

    public string Detail => check.Detail;

    public string? Fix => check.Fix;

    public bool HasFix => !string.IsNullOrWhiteSpace(check.Fix);
}

/**
 * 新增或编辑一台机器的表单, 覆盖 MachineSpec 的全部字段。
 * 本机条目只用名称与 harness home; SSH 条目用主机、端口、用户、认证方式(密码/密钥/agent)、密钥路径、ProxyCommand、远端目录、远端 harness home 与额外 ssh 选项(每行一个 Key=Value)。
 */
public sealed partial class MachineEditorViewModel : ObservableObject
{
    private const int MaxPort = 65535;

    public static IReadOnlyList<string> AuthModes { get; } = [SshAuth.Agent, SshAuth.Key, SshAuth.Password];

    public static IReadOnlyList<string> Kinds { get; } = [MachineKinds.Ssh, MachineKinds.Local];

    /** 编辑已有机器时的原名称(改名保存时替换原记录); 新增时为 null。 */
    public string? OriginalName { get; init; }

    public bool IsNew => OriginalName is null;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSsh))]
    private string _kind = MachineKinds.Ssh;

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private string _port = MachineSpec.DefaultSshPort.ToString();

    [ObservableProperty]
    private string _user = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPassword), nameof(IsKey))]
    private string _auth = SshAuth.Agent;

    [ObservableProperty]
    private string _keyPath = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _proxy = "";

    [ObservableProperty]
    private string _remotePath = "";

    [ObservableProperty]
    private string _harnessHome = "";

    /** 额外的 ssh `-o` 选项, 每行一个 Key=Value。 */
    [ObservableProperty]
    private string _sshOptions = "";

    public bool IsSsh => Kind == MachineKinds.Ssh;

    public bool IsPassword => Auth == SshAuth.Password;

    public bool IsKey => Auth == SshAuth.Key;

    public static MachineEditorViewModel From(MachineSpec spec) => new()
    {
        OriginalName = spec.Name,
        Name = spec.Name,
        Kind = spec.Kind,
        Host = spec.Host,
        Port = spec.Port.ToString(),
        User = spec.User,
        Auth = spec.Auth,
        KeyPath = spec.KeyPath ?? "",
        Password = spec.Password ?? "",
        Proxy = spec.Proxy ?? "",
        RemotePath = spec.RemotePath ?? "",
        HarnessHome = spec.HarnessHome ?? "",
        SshOptions = string.Join(Environment.NewLine, spec.SshOptions ?? []),
    };

    /** 表单 → 机器记录; 字段不完整时抛 ArgumentException(名称规则由 MachineStore 保存时再校验)。 */
    public MachineSpec ToSpec()
    {
        var name = Name.Trim();
        if (!IsSsh)
            return MachineSpec.Local(Optional(HarnessHome) ?? throw new ArgumentException("本机条目需要填写 harness home（默认的本机已经隐式存在）"), name);
        if (!int.TryParse(Port.Trim(), out var port) || port is <= 0 or > MaxPort)
            throw new ArgumentException($"端口必须是 1-{MaxPort} 的整数");
        string[] options = [.. SshOptions.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
        return MachineSpec.Ssh(name, Host.Trim(), port, User.Trim(), Auth) with
        {
            KeyPath = IsKey ? Optional(KeyPath) : null,
            Password = IsPassword ? Password : null,
            Proxy = Optional(Proxy),
            RemotePath = Optional(RemotePath),
            HarnessHome = Optional(HarnessHome),
            SshOptions = options.Length == 0 ? null : options,
        };
    }

    /** 空白输入表示不设置(null), 否则去掉首尾空白。 */
    internal static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/**
 * 自动部署偏好(与机器清单存在同一个 machines.json 里, 所有 SSH 机器共用)的表单。
 * 下载代理用于本机下载, 远端代理在远端解析, 用于远端直接下载。
 */
public sealed partial class DeploySettingsEditorViewModel : ObservableObject
{
    public static IReadOnlyList<string> ViaOptions { get; } = [DownloadVia.Auto, DownloadVia.Client, DownloadVia.Server];

    [ObservableProperty]
    private bool _autoDeploy = true;

    [ObservableProperty]
    private string _releaseBaseUrl = DeploySettings.DefaultReleaseBaseUrl;

    [ObservableProperty]
    private string _releaseVersion = DeploySettings.LatestVersion;

    [ObservableProperty]
    private string _downloadProxy = "";

    [ObservableProperty]
    private string _via = DownloadVia.Auto;

    [ObservableProperty]
    private string _serverProxy = "";

    public static DeploySettingsEditorViewModel From(DeploySettings settings) => new()
    {
        AutoDeploy = settings.AutoDeploy,
        ReleaseBaseUrl = settings.ReleaseBaseUrl,
        ReleaseVersion = settings.ReleaseVersion,
        DownloadProxy = settings.DownloadProxy ?? "",
        Via = settings.Via,
        ServerProxy = settings.ServerProxy ?? "",
    };

    public DeploySettings ToSettings() => new()
    {
        AutoDeploy = AutoDeploy,
        ReleaseBaseUrl = MachineEditorViewModel.Optional(ReleaseBaseUrl) ?? DeploySettings.DefaultReleaseBaseUrl,
        ReleaseVersion = MachineEditorViewModel.Optional(ReleaseVersion) ?? DeploySettings.LatestVersion,
        DownloadProxy = MachineEditorViewModel.Optional(DownloadProxy),
        Via = ViaOptions.Contains(Via) ? Via : DownloadVia.Auto,
        ServerProxy = MachineEditorViewModel.Optional(ServerProxy),
    };
}
