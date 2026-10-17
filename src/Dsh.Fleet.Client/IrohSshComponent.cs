namespace Dsh.Fleet;

/** Resolves the version-pinned component distributed with every Fleet client. */
public static class IrohSshComponent
{
    private static readonly string Executable = Path.Combine(
        AppContext.BaseDirectory, "tools", OperatingSystem.IsWindows() ? "iroh-ssh.exe" : "iroh-ssh");

    public static string RequireExecutable()
    {
        if (!File.Exists(Executable))
            throw new FileNotFoundException("The bundled iroh-ssh component is missing; rebuild or reinstall the complete dsharp distribution.", Executable);
        return Executable;
    }
}
