using System.Reflection;
// args: managedDir outFile assembly...
var dir = args[0];
var paths = Directory.GetFiles(dir, "*.dll").ToList();
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "mscorlib");
var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
var lines = new List<string>();
foreach (var name in args.Skip(2))
{
    var asm = mlc.LoadFromAssemblyPath(Path.Combine(dir, name + ".dll"));
    Type[] types;
    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
    foreach (var t in types)
    {
        lines.Add($"{name} T {t.FullName} : {t.BaseType?.FullName}");
        MemberInfo[] ms;
        try { ms = t.GetMembers(flags); } catch { lines.Add($"{name} ! {t.FullName} members unreadable"); continue; }
        foreach (var m in ms)
        {
            string s;
            try { s = m.ToString(); } catch { s = m.Name + " <unreadable>"; }
            if (m is FieldInfo f && f.IsLiteral) { try { s += " = " + f.GetRawConstantValue(); } catch { } }
            lines.Add($"{name} M {t.FullName} :: {m.MemberType} {s}");
        }
    }
}
lines.Sort(StringComparer.Ordinal);
File.WriteAllLines(args[1], lines);
Console.WriteLine($"{lines.Count} lines");
