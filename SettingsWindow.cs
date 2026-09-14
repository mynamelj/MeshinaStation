using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MeshinaStandalone
{
    public sealed class SettingsWindow : Window
    {
        public SettingsWindow(string directory)
        {
            Title = "啮合配置 · 保留原JSON格式"; Width = 850; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(14) }; Content = root;
            var save = new Button { Content = "校验并保存", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(save, Dock.Bottom); root.Children.Add(save);
            var hint = new TextBlock { Text = "只读取以下五份配置。按StationNumber匹配工位；所有工位统一使用COM串口扫码。\n字段映射位于meshina.json的ItemNames。保存前会备份原配置；保存后返回主界面启动生效。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
            var tabs = new TabControl(); root.Children.Add(tabs);
            var editors = new Dictionary<string, TextBox>();
            foreach (string name in Configuration.FileNames)
            {
                var editor = new TextBox { AcceptsReturn = true, AcceptsTab = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
                string path = Path.Combine(directory, name);
                try { editor.Text = File.Exists(path) ? File.ReadAllText(path) : ""; }
                catch (Exception ex) { editor.Text = ""; hint.Text = "读取失败：" + ex.Message; }
                editors.Add(name, editor); tabs.Items.Add(new TabItem { Header = name, Content = editor });
            }
            save.Click += (s, e) =>
            {
                try
                {
                    var values = editors.ToDictionary(kv => kv.Key, kv => kv.Value.Text);
                    _ = Configuration.Parse(values);
                    Directory.CreateDirectory(directory);
                    string backup = Path.Combine(directory, "backups", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                    Directory.CreateDirectory(backup);
                    foreach (string name in Configuration.FileNames)
                        if (File.Exists(Path.Combine(directory, name))) File.Copy(Path.Combine(directory, name), Path.Combine(backup, name));
                    try { foreach (var kv in values) File.WriteAllText(Path.Combine(directory, kv.Key), kv.Value); }
                    catch
                    {
                        foreach (string name in Configuration.FileNames)
                            if (File.Exists(Path.Combine(backup, name))) File.Copy(Path.Combine(backup, name), Path.Combine(directory, name), true);
                        throw;
                    }
                    DialogResult = true;
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "配置未保存"); }
            };
        }
    }
}
