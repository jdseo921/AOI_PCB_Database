using System.Windows.Controls;

namespace AOI_Monitor.Services;

/// <summary>
/// UiPreferencesService.ApplyLocalization rewrites ComboBoxItem.Content for display, so any
/// value that is persisted, compared, or mapped to logic must come from the invariant token in
/// Tag. Content is used as the token only for items that never carry a Tag.
/// </summary>
public static class ComboBoxTokens
{
    public static string? Token(ComboBoxItem? item)
        => item?.Tag is string tag && !string.IsNullOrWhiteSpace(tag)
            ? tag
            : item?.Content as string;

    public static string SelectedToken(ComboBox? combo, string fallback)
        => Token(combo?.SelectedItem as ComboBoxItem) ?? fallback;

    public static bool SelectByToken(ComboBox combo, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem candidate &&
                string.Equals(Token(candidate), token, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = candidate;
                return true;
            }
        }

        return false;
    }
}
