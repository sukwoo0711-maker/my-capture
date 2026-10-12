using System.IO;
using System.Reflection;
using System.Runtime.Loader;

internal static class Bootstrap
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine("TutorialGifGenerator <published-payload-folder> <new-output-folder> [comma-separated-feature-ids]");
            return 2;
        }
        try
        {
            string payload = Path.GetFullPath(args[0]);
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", payload + Path.DirectorySeparatorChar);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string path = Path.Combine(payload, name.Name + ".dll");
                return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
            };
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(payload, "MyCapture.dll"));
            return (int)typeof(Bootstrap).Assembly.GetType("Program", true)!
                .GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [args])!;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 3;
        }
    }
}
