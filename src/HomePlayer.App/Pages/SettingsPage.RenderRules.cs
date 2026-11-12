using HomePlayer.Core;
using HomePlayer.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class SettingsPage
{
    private void RefreshRenderRulesControls()
    {
        var settings = App.Host.Settings;
        RenderRulesSwitch.IsOn = settings.RenderRulesEnabled;
        RenderProfileOverrideBox.IsEnabled = settings.RenderRulesEnabled;
        RenderProfileOverrideBox.Items.Clear();
        RenderProfileOverrideBox.Items.Add(new ComboBoxItem { Content = "自动（按规则）", Tag = "" });
        foreach (var profile in App.Host.RenderRules.Profiles)
            RenderProfileOverrideBox.Items.Add(new ComboBoxItem
            {
                Content = BuiltInRenderPresets.DisplayName(profile.Id),
                Tag = profile.Id
            });
        RenderProfileOverrideBox.SelectedItem = RenderProfileOverrideBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, settings.RenderProfileOverride,
                StringComparison.OrdinalIgnoreCase)) ?? RenderProfileOverrideBox.Items[0];
        RefreshRenderRulesStatus();
        RefreshRenderParameters();
    }

    private void RefreshRenderRulesStatus()
    {
        var service = App.Host.RenderRules;
        var status = service.ActiveStatus;
        var text = $"目录：{service.Store.Root}\n仅支持 libplacebo 内置输出；Basic、madVR 和 MPC VR 不执行 Shader。";
        if (!string.IsNullOrWhiteSpace(service.Warning))
            text += "\n配置未更新，继续保留上一套可用规则：" + service.Warning;
        if (status != null)
        {
            text += $"\n当前配置：{status.ProfileId ?? "无"}；Shader {status.Shaders.Count} 个";
            if (status.CandidatePending)
                text += "（等待首次成功渲染）";
            if (!string.IsNullOrWhiteSpace(status.Warning))
                text += "\n" + status.Warning;
        }
        RenderRulesStatusText.Text = text;
    }

    private void RefreshRenderParameters()
    {
        RenderParametersPanel.Children.Clear();
        var status = App.Host.RenderRules.ActiveStatus;
        if (status?.ProfileId == null || status.Shaders.Count == 0
            || BuiltInRenderPresets.IsBuiltIn(status.ProfileId))
            return;
        var profile = App.Host.RenderRules.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, status.ProfileId, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
            return;
        foreach (var shader in status.Shaders)
        {
            if (shader.Parameters.Count == 0)
                continue;
            RenderParametersPanel.Children.Add(new TextBlock
            {
                Text = shader.File,
                Style = (Style)Resources["SubtitleTextBlockStyle"]
            });
            var definition = profile.Shaders.FirstOrDefault(candidate =>
                string.Equals(candidate.File, shader.File, StringComparison.OrdinalIgnoreCase));
            foreach (var parameter in shader.Parameters)
            {
                var value = definition != null && definition.Parameters.TryGetValue(parameter.Name, out var configured)
                    ? configured : parameter.Initial;
                RenderParametersPanel.Children.Add(CreateParameterEditor(status.ProfileId, shader, parameter, value));
            }
        }
    }

    private FrameworkElement CreateParameterEditor(
        string profileId,
        LibplaceboLoadedShader shader,
        LibplaceboShaderParameter parameter,
        double value)
    {
        var title = string.IsNullOrWhiteSpace(parameter.Description) ? parameter.Name : parameter.Description;
        if (parameter.Names.Count > 0)
        {
            var combo = new ComboBox { Header = title, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var name in parameter.Names)
                combo.Items.Add(new ComboBoxItem { Content = name });
            var selected = (int)value - (int)parameter.Minimum;
            combo.SelectedIndex = selected >= 0 && selected < parameter.Names.Count ? selected : 0;
            combo.SelectionChanged += (_, _) =>
            {
                if (_loading || combo.SelectedIndex < 0)
                    return;
                SaveRenderParameter(profileId, shader.File, parameter.Name, parameter.Minimum + combo.SelectedIndex);
            };
            return combo;
        }
        var number = new NumberBox
        {
            Header = title,
            Value = value,
            SmallChange = parameter.Type is 1 or 2 ? 1 : 0.1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        if (parameter.Maximum > parameter.Minimum)
        {
            number.Minimum = parameter.Minimum;
            number.Maximum = parameter.Maximum;
        }
        number.ValueChanged += (_, args) =>
        {
            if (_loading || double.IsNaN(args.NewValue))
                return;
            SaveRenderParameter(profileId, shader.File, parameter.Name, args.NewValue);
        };
        return number;
    }

    private void SaveRenderParameter(string profileId, string shader, string name, double value)
    {
        try
        {
            App.Host.RenderRules.UpdateParameter(profileId, shader, name, value);
            RefreshRenderRulesStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RenderRulesStatusText.Text = "参数保存失败：" + ex.Message;
        }
    }

    private async void ImportShader_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".glsl");
            picker.FileTypeFilter.Add(".hook");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
            var file = await picker.PickSingleFileAsync();
            if (file == null)
                return;
            var imported = await Task.Run(() => App.Host.RenderRules.ImportShader(file.Path));
            RenderRulesStatusText.Text = $"已导入 {imported}。请在 rules.json 的 Profile 中引用后点击“重新加载”。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RenderRulesStatusText.Text = "Shader 导入失败：" + ex.Message;
        }
    }

    private void ReloadRenderRules_Click(object sender, RoutedEventArgs e)
    {
        App.Host.RenderRules.Reload();
        _loading = true;
        try
        {
            RefreshRenderRulesControls();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OpenRenderRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.Host.RenderRules.Store.ShaderDirectory);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{App.Host.RenderRules.Store.Root}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RenderRulesStatusText.Text = "无法打开目录：" + ex.Message;
        }
    }
}
