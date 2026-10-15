namespace Dsh.Plugins.Native;

/** 插件代码在受护调用里触发了原生故障(段错误/非法指令等)并被 dsh_plugin_guard 接住;宿主据此标记插件 Faulted 并卸载。 */
public sealed class NativePluginFaultException(string message) : Exception(message);
