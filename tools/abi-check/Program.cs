// Usage: dotnet run --project tools/abi-check -p:TF=net9.0 -p:JF=10.11.0 -p:HA=9.0.11 -p:PluginDll=<built HtspTuner.dll>
using System.Reflection;
using System.Runtime.CompilerServices;
// Forces the JIT over every method in the plugin against whatever Jellyfin this was restored with:
// a member the plugin binds to that this server lacks surfaces as MissingMethod/TypeLoad here.
var asm = typeof(HtspTuner.Plugin).Assembly;
Console.WriteLine("Jellyfin " + typeof(MediaBrowser.Controller.Library.IUserManager).Assembly.GetName().Version);
int ok = 0, bad = 0;
Type[] types;
try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { foreach (var le in e.LoaderExceptions) Console.WriteLine("LOAD " + le?.Message); return 1; }
foreach (var t in types)
{
    if (t.ContainsGenericParameters) continue;
    foreach (var m in t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Cast<MethodBase>()
        .Concat(t.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
    {
        if (m.IsAbstract || m.ContainsGenericParameters) continue;
        try { RuntimeHelpers.PrepareMethod(m.MethodHandle); ok++; }
        catch (Exception e) { bad++; Console.WriteLine($"FAIL {t.FullName}.{m.Name}: {e.GetType().Name} {e.Message}"); }
    }
}
Console.WriteLine($"jitted {ok}, failed {bad}");
return bad;
