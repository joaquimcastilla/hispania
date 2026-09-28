namespace Hispania.Runtime;

public enum ModuleLifecycleState
{
    Registered,
    Starting,
    Running,
    Degraded,
    Draining,
    Stopping,
    Stopped,
    Failed
}
