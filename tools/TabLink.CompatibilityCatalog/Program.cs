using TabLink.CompatibilityCatalog;

try
{
    var options = CommandLineOptions.Parse(args);
    if (options.ShowHelp)
    {
        CommandLineOptions.WriteUsage(Console.Out);
        return 0;
    }

    var workspace = new CompatibilityCatalogWorkspace(options.RepositoryRoot);
    if (options.Write)
    {
        workspace.WriteGeneratedFiles();
        Console.WriteLine("Compatibility schema and public catalog were regenerated.");
    }
    else
    {
        workspace.CheckGeneratedFiles();
        Console.WriteLine("Compatibility catalog, schema and public catalog are valid and current.");
    }

    return 0;
}
catch (CommandLineException exception)
{
    Console.Error.WriteLine(exception.Message);
    CommandLineOptions.WriteUsage(Console.Error);
    return 2;
}
catch (InvalidDataException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Compatibility catalog files could not be read or written.");
    return 1;
}

internal sealed record CommandLineOptions(string RepositoryRoot, bool Write, bool ShowHelp)
{
    public static CommandLineOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var repositoryRoot = Directory.GetCurrentDirectory();
        var write = false;
        var modeSeen = false;
        var rootSeen = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root":
                    if (rootSeen || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                        throw new CommandLineException("--root must be specified once with one directory argument.");
                    repositoryRoot = args[++index];
                    rootSeen = true;
                    break;
                case "--check":
                    if (modeSeen)
                        throw new CommandLineException("Specify only one of --check or --write.");
                    write = false;
                    modeSeen = true;
                    break;
                case "--write":
                    if (modeSeen)
                        throw new CommandLineException("Specify only one of --check or --write.");
                    write = true;
                    modeSeen = true;
                    break;
                case "--help" or "-h":
                    if (args.Length != 1)
                        throw new CommandLineException("--help cannot be combined with other arguments.");
                    return new CommandLineOptions(repositoryRoot, false, true);
                default:
                    throw new CommandLineException("An unsupported command-line option was supplied.");
            }
        }

        return new CommandLineOptions(repositoryRoot, write, false);
    }

    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("TabLink.CompatibilityCatalog [--root <repository-root>] [--check | --write]");
        writer.WriteLine("  --check  Validate compatibility/catalog.json and require generated files to match (default).");
        writer.WriteLine("  --write  Validate the catalog, then atomically regenerate its schema and README.");
    }
}

internal sealed class CommandLineException(string message) : Exception(message);
