// Sandbox-compatible test runner.
//
// `dotnet test` (vstest) cannot start its testhost in this sandbox — the
// testhost IPC channel is blocked, so the run times out after 300s. The
// tests themselves are standard xUnit ([Fact], Xunit.Assert) and `dotnet
// test` works on a normal machine (Rishi's machine, CI). This runner is
// the way to execute them HERE: it reflects over the Tests assembly,
// invokes every [Fact]/[Theory] method, and prints a pass/fail summary.
// Exit code 0 = all pass, 1 = any failure.
//
// Usage (from BroadcastContentAutomation/):
//   dotnet run --project Tests.Runner -c Release
//   dotnet run --project Tests.Runner -c Release -- <filter-substring>
using System.Reflection;

var filter = args.Length > 0 ? args[0] : null;
var testsAssembly = typeof(BroadcastContentAutomation.Tests.TextTests).Assembly;
var passed = 0;
var failed = 0;
var failures = new List<string>();

foreach (var type in testsAssembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.IsPublic))
{
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
    {
        var isFact = method.GetCustomAttributes().Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute");
        if (!isFact) continue;
        var name = $"{type.Name}.{method.Name}";
        if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
        // Theories need InlineData; the runner skips parameterized methods
        // (the suite's parameterized cases are separate [Fact] methods).
        // Report them as skipped, not passed.
        if (method.GetParameters().Length > 0)
        {
            Console.WriteLine($"SKIP {name} (parameterized — run via dotnet test)");
            continue;
        }
        try
        {
            var instance = Activator.CreateInstance(type)!;
            // Async [Fact]s return a Task: await it, or a post-await
            // assertion failure is silently swallowed and the test
            // reports PASS it did not earn. (Found 2026-11-24 while
            // adding async image-pool tests — the old code dropped the
            // Task entirely.)
            if (method.Invoke(instance, null) is Task task)
                task.GetAwaiter().GetResult();
            Console.WriteLine($"PASS {name}");
            passed++;
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            Console.WriteLine($"FAIL {name}: {inner.Message}");
            failures.Add($"{name}: {inner.Message}");
            failed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.Message}");
            failures.Add($"{name}: {ex.Message}");
            failed++;
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Tests: {passed} passed, {failed} failed, {passed + failed} total.");
if (failures.Count > 0)
{
    Console.WriteLine("Failures:");
    foreach (var f in failures) Console.WriteLine($"  - {f}");
}
return failed == 0 ? 0 : 1;
