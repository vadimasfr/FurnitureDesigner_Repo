using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FurnitureDesigner.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Enter in any TextBox commits the value, so typed dimensions apply without tabbing away.
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.KeyDownEvent, new KeyEventHandler((sender, args) =>
        {
            if (args.Key == Key.Enter && sender is TextBox { AcceptsReturn: false } box)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                box.SelectAll();
            }
        }));
        base.OnStartup(e);
    }
}
