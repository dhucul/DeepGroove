using System.Windows;
using System.Windows.Controls;

namespace WaveLab.Views;

/// <summary>
/// A themed prompt offering several labelled courses of action, returning which was chosen.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MessageBox"/> cannot label its buttons, so a three-way decision through it has to be
/// worded as a question with a Yes and a No that mean things the buttons do not say. Here each
/// button carries what it will do, which is what lets the message state the situation rather than
/// re-state the options.
/// </para>
/// <para>
/// Like <see cref="InfoDialog"/> and unlike <see cref="MessageBox"/>, this is a real
/// <see cref="Window"/> shown with <c>ShowDialog</c>, so it disables the whole application rather
/// than its owner alone — which matters wherever a modeless panel could move the document out from
/// under the decision being made.
/// </para>
/// </remarks>
public partial class ChoiceDialog : Window
{
    /// <summary>Index into the labels the dialog was built with, or −1 if it was dismissed.</summary>
    public int Choice { get; private set; } = -1;

    public ChoiceDialog(string title, string message, params string[] labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Length == 0) throw new ArgumentException("A choice needs at least one option.", nameof(labels));

        InitializeComponent();
        titleText.Text = title;
        messageText.Text = message;

        for (int index = 0; index < labels.Length; index++)
        {
            int chosen = index;
            bool last = index == labels.Length - 1;
            var button = new Button
            {
                // The first option is the safest one and takes the accent, so the default answer
                // and the emphasised one are the same button rather than two different ones.
                Style = (Style)FindResource(index == 0 ? "PrimaryChoiceButton" : "ChoiceButton"),
                Content = new TextBlock { Text = labels[index], TextWrapping = TextWrapping.Wrap },
                IsDefault = index == 0,
                // Escape lands on the last option, which is Cancel wherever there is one. With no
                // cancelling option the dialog still has to be dismissable, and −1 says so.
                IsCancel = last,
            };
            button.Click += (_, _) => { Choice = chosen; DialogResult = true; Close(); };
            buttons.Children.Add(button);
        }
    }

    /// <summary>Asks, and returns the chosen index, or −1 if the dialog was dismissed.</summary>
    public static int Ask(Window owner, string title, string message, params string[] labels)
    {
        var dialog = new ChoiceDialog(title, message, labels) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }
}
