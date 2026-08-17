using System.Reflection;
using System.Windows.Forms;

// 运行时集成验证：Form播放列表 的 DragSelectZoneWidth 跟随窗口尺寸
var thread = new Thread(() =>
{
    try
    {
        var appDir = @"C:\PLAN\3FCompare\third_party\fff_project\FFF.Player\bin\Release\net10.0-windows10.0.26100.0";
        Directory.SetCurrentDirectory(appDir);
        var asm = Assembly.LoadFrom(Path.Combine(appDir, "FFF.Player.dll"));
        var formType = asm.GetType("FFF.Player.Form播放列表", throwOnError: false);
        if (formType == null)
        {
            Console.WriteLine("❌ 未找到 Form播放列表 类型");
            foreach (var t in asm.GetExportedTypes().Where(t => t.Name.Contains("播放")))
                Console.WriteLine($"  候选: {t.FullName}");
            return;
        }
        Console.WriteLine($"✅ 类型: {formType.FullName}");

        var form = (Form)Activator.CreateInstance(formType)!;
        try
        {
            var listField = formType.GetField("_UltraDetailListView1",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? formType.GetField("UltraDetailListView1",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (listField == null)
            {
                // 列出所有 ListView 相关字段帮助诊断
                Console.WriteLine("❌ 未找到 UltraDetailListView1 字段，全部字段：");
                foreach (var f in formType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                    if (f.Name.Contains("List") || f.FieldType.Name.Contains("List"))
                        Console.WriteLine($"  {f.Name} : {f.FieldType.Name}");
                return;
            }
            var list = (Control)listField.GetValue(form)!;
            var zoneProp = list.GetType().GetProperty("DragSelectZoneWidth")!;
            var dpiProp = list.GetType().GetProperty("DeviceDpi")!;

            form.ClientSize = new Size(800, 600);
            list.Size = new Size(780, 500);
            var method = formType.GetMethod("更新列表列宽",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                Console.WriteLine("❌ 未找到 更新列表列宽 方法");
                return;
            }
            method.Invoke(form, null);

            var dpi = (int)dpiProp.GetValue(list)!;
            var zone = (int)zoneProp.GetValue(list)!;
            var clientW = list.ClientSize.Width;
            var expected = Math.Max(40, (int)(clientW / 2.0 / (dpi / 96.0)));
            Console.WriteLine($"DPI={dpi} ClientWidth={clientW}");
            Console.WriteLine($"DragSelectZoneWidth 实际={zone} 期望={expected}");
            Console.WriteLine(zone == expected
                ? "✅ DragSelectZoneWidth = 右半区宽度，计算正确"
                : "❌ DragSelectZoneWidth 计算错误");

            // 窄窗口保护验证：缩小表单（Dock=Fill 列表随之缩小）
            form.ClientSize = new Size(120, 600);
            method.Invoke(form, null);
            var zoneSmall = (int)zoneProp.GetValue(list)!;
            var clientSmall = list.ClientSize.Width;
            Console.WriteLine($"窄窗口: ClientWidth={clientSmall} DragSelectZoneWidth={zoneSmall}");
            Console.WriteLine(zoneSmall >= 40 && zoneSmall <= clientSmall
                ? "✅ 最小宽度 40 保护且不超过客户区"
                : "❌ 最小宽度保护失效");

            var multi = (bool)list.GetType().GetProperty("MultiSelect")!.GetValue(list)!;
            var reorder = (bool)list.GetType().GetProperty("AllowDragReorder")!.GetValue(list)!;
            Console.WriteLine($"MultiSelect={multi} AllowDragReorder={reorder}");
            Console.WriteLine(multi
                ? "✅ 框选前提 MultiSelect=True 满足"
                : "❌ MultiSelect 未启用，右半区框选不可用");
        }
        finally
        {
            form.Dispose();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ 测试异常: {ex}");
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join(30000);
Console.WriteLine("集成验证完成");
return 0;
