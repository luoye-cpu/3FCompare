using System;
using _3FCompare.Core.Display;
class Program {
    public static void Main() {
        Console.WriteLine("调用 GpuEnumeration.Enumerate...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try {
            var list = GpuEnumeration.Enumerate();
            sw.Stop();
            Console.WriteLine($"完成: {list.Count} 个适配器, 耗时 {sw.ElapsedMilliseconds}ms");
            foreach (var a in list) Console.WriteLine($"  [{a.Index}] {a.Description}");
        } catch (Exception ex) {
            Console.WriteLine($"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
