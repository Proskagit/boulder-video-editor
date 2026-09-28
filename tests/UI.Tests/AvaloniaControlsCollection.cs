using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Test classes that create Avalonia controls (<c>new Button()</c>, <c>new TextBox()</c>, …) run one after another:
/// Avalonia's property metadata caches are not thread-safe, and two classes building their first controls on
/// different threads at once corrupted them — an intermittent <see cref="System.NullReferenceException"/> in
/// <c>AvaloniaProperty.GetMetadataWithOverrides</c> or a test run that never ends (found in D024 Step 9.8).
/// </summary>
[CollectionDefinition(Name)]
public sealed class AvaloniaControlsCollection
{
    public const string Name = "Avalonia controls";
}
