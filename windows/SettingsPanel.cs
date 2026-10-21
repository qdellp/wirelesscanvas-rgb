namespace WirelessCanvas;

/// <summary>Application preferences are separate from hardware/RGB profiles.</summary>
public sealed class SettingsPanel : UserControl
{
    public sealed record LanguageChoice(string Code, string Label)
    {
        public override string ToString() => Label;
    }
    public SettingsPanel(CheckBox startup, AppPreferences preferences, Action save)
    {
        Name = "settingsPanel"; Dock = DockStyle.Fill; BackColor = Ui.Background; ForeColor = Ui.Text;
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(12) };
        Controls.Add(body);
        body.Controls.Add(new Label { Text = L10n.T("Settings"), AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold), Margin = new(0, 8, 0, 22) });
        startup.Name = "startWithWindows"; startup.Margin = new(0, 0, 0, 22); startup.MaximumSize = new(850, 0); body.Controls.Add(startup);
        body.Controls.Add(new Label { Text = L10n.T("Interface language"), AutoSize = true, Margin = new(0, 0, 0, 8) });
        var languages = new ComboBox { Name = "interfaceLanguage", DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, Margin = new(0, 0, 0, 16) };
        LanguageChoice[] choices = [new("system", L10n.T("Follow Windows")), new("en", "English"), new("ru", "Русский"), new("zh-Hans", "简体中文"), new("es", "Español"), new("pt", "Português")];
        languages.Items.AddRange(choices);
        languages.SelectedItem = choices.FirstOrDefault(c => c.Code == preferences.Language) ?? choices[0];
        body.Controls.Add(languages);
        var hint = new Label { Text = L10n.T("Language changes take effect after restarting the app. Lighting and cooling settings are preserved."), MaximumSize = new(850, 0), AutoSize = true };
        body.Controls.Add(hint);
        languages.SelectedIndexChanged += (_, _) =>
        {
            if (languages.SelectedItem is not LanguageChoice choice) return;
            if (choice.Code == preferences.Language) return;
            string old = preferences.Language; preferences.Language = choice.Code;
            try { save(); hint.Text = L10n.T("Language saved. Restart the app when convenient."); }
            catch (Exception e) { preferences.Language = old; languages.SelectedItem = choices.First(c => c.Code == old); hint.Text = e.Message; }
        };
    }
}
