// Helpers for reaching the project's internals from tests.
//
// Most of the pipeline's pure logic lives in one of two places that a test
// project cannot reference directly:
//   * VideoGen/Program.cs — top-level local functions (BuildChapters,
//     CanonicalHashtags, WithCanonicalHashtags, ...). They are private
//     methods on the generated Program class with mangled names
//     ("<<Main>$>g__BuildChapters|0_33"). Found by substring, the same
//     pattern the fix-verification harnesses used.
//   * ScriptGenerator / MetaGraph / publishers — internal static methods
//     and internal types. Found by name on the real type.
// Shared (Text, SeoScore, Sidecar) is public and is referenced directly;
// use this helper only for the two cases above.
//
// If a pure function is ever extracted to Shared as a public static method,
// prefer calling it directly and drop the reflection lookup for it.
using System.Reflection;

namespace BroadcastContentAutomation.Tests;

internal static class ReflectionHelper
{
    private static readonly Lazy<Assembly> VideoGenAssembly = new(() =>
        Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "VideoGen.dll")));

    private static readonly Lazy<Assembly> UploaderAssembly = new(() =>
        Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Uploader.dll")));

    private static readonly Lazy<Assembly> SharedAssembly = new(() =>
        Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "GiggleGarden.Shared.dll")));

    public static Assembly VideoGen => VideoGenAssembly.Value;
    public static Assembly Uploader => UploaderAssembly.Value;
    public static Assembly Shared => SharedAssembly.Value;

    // Find a (possibly mangled) static method whose name contains the given
    // substring, searching every type in the assembly. Throws when absent so
    // a renamed/deleted function fails loudly instead of silently skipping.
    public static MethodInfo FindMethod(Assembly assembly, string namePart)
    {
        var method = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            .FirstOrDefault(m => m.Name.Contains(namePart, StringComparison.Ordinal));
        return method ?? throw new MissingMethodException(
            $"No static method containing '{namePart}' found in {assembly.GetName().Name}. " +
            "It was renamed or deleted — update the test, do not delete it silently.");
    }

    // Find a type by its (unqualified) name.
    public static Type FindType(Assembly assembly, string name)
    {
        var type = assembly.GetTypes().FirstOrDefault(t => t.Name == name);
        return type ?? throw new TypeLoadException(
            $"No type named '{name}' found in {assembly.GetName().Name}.");
    }

    // Invoke a static method found by substring and cast the result.
    public static T Invoke<T>(Assembly assembly, string namePart, params object?[] args)
    {
        var method = FindMethod(assembly, namePart);
        return (T)method.Invoke(null, args)!;
    }

    // The Trendline Now ContentProfile (TrendlineNowProfile.Value) — the live
    // profile object, so tests assert against the real BaseTags, not a copy.
    public static object TrendlineNowProfile()
    {
        var profileType = FindType(VideoGen, "TrendlineNowProfile");
        var field = profileType.GetField("Value", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("TrendlineNowProfile.Value");
        return field.GetValue(null)!;
    }

    public static string[] TrendlineNowBaseTags()
    {
        var profile = TrendlineNowProfile();
        var prop = profile.GetType().GetProperty("BaseTags")
            ?? throw new MissingMemberException("ContentProfile.BaseTags");
        return (string[])prop.GetValue(profile)!;
    }
}
