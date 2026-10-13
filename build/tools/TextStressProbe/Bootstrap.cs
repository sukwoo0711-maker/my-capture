using System.IO;
using System.Reflection;
using System.Runtime.Loader;

internal static class Bootstrap
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length is not (5 or 6))
        {
            Console.Error.WriteLine("TextStress <payload> <ocr|editor|editor-render|editor-hold|ocr-hold|ocr-editor-hold> <characters> <long|lines> <new-output.json> [diagnostic-options]");
            return 2;
        }
        try
        {
            string payload = Path.GetFullPath(args[0]);
            if (!File.Exists(Path.Combine(payload, "MyCapture.dll"))) throw new FileNotFoundException("Missing product payload.");
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", payload + Path.DirectorySeparatorChar);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string path = Path.Combine(payload, name.Name + ".dll");
                return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
            };
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(payload, "MyCapture.dll"));
            // Reflect into the workload only after installing the payload resolver. No product App.Run.
            return (int)typeof(Bootstrap).Assembly.GetType("Program", true)!
                .GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [args[1..]])!;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 3;
        }
    }
}
