namespace Dsh.Runtime;

/**
 * Marks a service method, or every method of a service interface, as reachable from out-of-process callers such as the SDK `dsh/service.call` bridge.
 * Members without this marker stay private to the process even when they are public.
 */
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Interface, Inherited = false)]
public sealed class RemoteCallableAttribute : Attribute;
