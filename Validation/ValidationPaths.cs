namespace CodexAccountSwitcher.Validation;

/// <summary>Locates the source checkout from the test executable, never from an account home.</summary>
internal static class ValidationPaths
{
    internal static string OutputDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
            {
                if (!File.Exists(Path.Combine(directory.FullName, "CodexAccountSwitcher.csproj")) ||
                    !Directory.Exists(Path.Combine(directory.FullName, "Validation"))) continue;
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked validation checkout refused.");
                return Path.Combine(directory.FullName, "validation-output");
            }
            throw new IOException("Run isolated validation from a built source checkout.");
        }
    }
}
