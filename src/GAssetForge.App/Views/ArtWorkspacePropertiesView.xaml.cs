using GAssetForge.App.ViewModels;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GAssetForge.App.Views;

public partial class ArtWorkspacePropertiesView : UserControl
{
    private ArtWorkspaceViewModel? _observedViewModel;
    private bool _synchronizingInstructionEditor;

    public ArtWorkspacePropertiesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ObserveViewModel(DataContext as ArtWorkspaceViewModel);
        RenderInstructionEditor();
        ScrollConversationToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ObserveViewModel(null);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ObserveViewModel(e.NewValue as ArtWorkspaceViewModel);
        }
    }

    private void ObserveViewModel(ArtWorkspaceViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
        {
            return;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel.Conversation.CollectionChanged -= OnConversationChanged;
            foreach (var message in _observedViewModel.Conversation)
            {
                message.PropertyChanged -= OnConversationMessagePropertyChanged;
            }
        }

        _observedViewModel = viewModel;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _observedViewModel.Conversation.CollectionChanged += OnConversationChanged;
            foreach (var message in _observedViewModel.Conversation)
            {
                message.PropertyChanged += OnConversationMessagePropertyChanged;
            }

            RenderInstructionEditor();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArtWorkspaceViewModel.InstructionDraftRevision) &&
            !_synchronizingInstructionEditor)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, RenderInstructionEditor);
        }
    }

    private void OnConversationChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var message in e.OldItems.OfType<ArtWorkspaceConversationMessageViewModel>())
            {
                message.PropertyChanged -= OnConversationMessagePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var message in e.NewItems.OfType<ArtWorkspaceConversationMessageViewModel>())
            {
                message.PropertyChanged += OnConversationMessagePropertyChanged;
            }
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollConversationToEnd);
    }

    private void OnConversationMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtWorkspaceConversationMessageViewModel.Text) or
            nameof(ArtWorkspaceConversationMessageViewModel.State))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollConversationToEnd);
        }
    }

    private void ScrollConversationToEnd() => ArtWorkspaceConversationScrollViewer.ScrollToEnd();

    private void ArtWorkspaceImageLayerList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ArtWorkspaceViewModel viewModel &&
            ArtWorkspaceImageLayerList.SelectedItem is ArtWorkspaceImageItemViewModel image)
        {
            viewModel.SelectImage(image);
        }
    }

    private void ArtWorkspaceInsertSelectedImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ArtWorkspaceViewModel viewModel ||
            viewModel.CreateSelectedImageReferenceDraftSegment() is not { } segment)
        {
            return;
        }

        ArtWorkspaceInstructionRichTextBox.Focus();
        if (!ArtWorkspaceInstructionRichTextBox.Selection.IsEmpty)
        {
            ArtWorkspaceInstructionRichTextBox.Selection.Text = string.Empty;
        }

        var insertionPosition = ArtWorkspaceInstructionRichTextBox.CaretPosition
            .GetInsertionPosition(LogicalDirection.Forward) ??
            ArtWorkspaceInstructionRichTextBox.Document.ContentEnd;
        InlineUIContainer container;
        try
        {
            container = CreateInstructionReferenceInline(segment, insertionPosition);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            var paragraph = EnsureTrailingInstructionParagraph();
            container = CreateInstructionReferenceInline(segment);
            paragraph.Inlines.Add(container);
        }

        ArtWorkspaceInstructionRichTextBox.CaretPosition =
            container.ElementEnd.GetInsertionPosition(LogicalDirection.Forward) ?? container.ElementEnd;
        SynchronizeInstructionEditor();
        ArtWorkspaceInstructionRichTextBox.Focus();
    }

    private void ArtWorkspaceClearInstructionReferencesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ArtWorkspaceViewModel viewModel)
        {
            viewModel.ClearInstructionDraftReferences();
            ArtWorkspaceInstructionRichTextBox.Focus();
        }
    }

    private void ArtWorkspaceInstructionRichTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        SynchronizeInstructionEditor();

    private void ArtWorkspaceInstructionRichTextBox_OnPasting(
        object sender,
        DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, autoConvert: true) ||
            e.SourceDataObject.GetData(DataFormats.UnicodeText, autoConvert: true) is not string text)
        {
            e.CancelCommand();
            return;
        }

        e.DataObject = new DataObject(DataFormats.UnicodeText, text);
        e.FormatToApply = DataFormats.UnicodeText;
    }

    private void ArtWorkspaceInstructionRichTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete &&
            ArtWorkspaceInstructionRichTextBox.Selection.IsEmpty &&
            TryRemoveAdjacentInstructionReference(e.Key == Key.Back
                ? LogicalDirection.Backward
                : LogicalDirection.Forward))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        SynchronizeInstructionEditor();
        if (DataContext is not ArtWorkspaceViewModel viewModel ||
            !viewModel.SubmitInstructionCommand.CanExecute(null))
        {
            e.Handled = true;
            return;
        }

        viewModel.SubmitInstructionCommand.Execute(null);
        e.Handled = true;
        ArtWorkspaceInstructionRichTextBox.Focus();
    }

    private InlineUIContainer CreateInstructionReferenceInline(
        ArtWorkspaceInstructionDraftSegment segment,
        TextPointer? insertionPosition = null)
    {
        var isValid = DataContext is ArtWorkspaceViewModel viewModel &&
                      viewModel.IsInstructionReferenceValid(segment);
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 9,
            Text = $"▣ {segment.DisplayText}",
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 210
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "App.TextPrimaryBrush");

        var removeButton = new Button
        {
            Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(3, 0, 3, 0),
            MinWidth = 18,
            Height = 18,
            Content = "×",
            ToolTip = "删除此图片引用标签",
            VerticalAlignment = VerticalAlignment.Center
        };
        if (TryFindResource("App.ButtonStyle") is Style buttonStyle)
        {
            removeButton.Style = buttonStyle;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(label);
        panel.Children.Add(removeButton);

        var border = new Border
        {
            Margin = new Thickness(2, 1, 2, 1),
            Padding = new Thickness(5, 2, 3, 2),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = panel,
            ToolTip = isValid
                ? segment.DisplayText
                : $"{segment.DisplayText}（图片已删除、隐藏或文件缺失）"
        };
        border.SetResourceReference(Border.BackgroundProperty, "App.InputBackgroundBrush");
        border.SetResourceReference(
            Border.BorderBrushProperty,
            isValid ? "App.AccentBrush" : "App.WarningBrush");

        var container = insertionPosition is null
            ? new InlineUIContainer(border)
            : new InlineUIContainer(border, insertionPosition);
        container.BaselineAlignment = BaselineAlignment.Center;
        container.Tag = segment.DeepClone();
        removeButton.Click += (_, _) =>
        {
            if (container.Parent is Paragraph paragraph)
            {
                paragraph.Inlines.Remove(container);
                SynchronizeInstructionEditor();
                ArtWorkspaceInstructionRichTextBox.Focus();
            }
        };
        return container;
    }

    private bool TryRemoveAdjacentInstructionReference(LogicalDirection direction)
    {
        var adjacent = ArtWorkspaceInstructionRichTextBox.CaretPosition.GetAdjacentElement(direction);
        if (adjacent is not InlineUIContainer { Tag: ArtWorkspaceInstructionDraftSegment } container ||
            container.Parent is not Paragraph paragraph)
        {
            return false;
        }

        paragraph.Inlines.Remove(container);
        SynchronizeInstructionEditor();
        return true;
    }

    private void RenderInstructionEditor()
    {
        if (_synchronizingInstructionEditor ||
            ArtWorkspaceInstructionRichTextBox is null ||
            DataContext is not ArtWorkspaceViewModel viewModel)
        {
            return;
        }

        _synchronizingInstructionEditor = true;
        try
        {
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            var paragraph = CreateInstructionParagraph();
            document.Blocks.Add(paragraph);
            foreach (var segment in viewModel.InstructionDraftSegments)
            {
                if (segment.IsText)
                {
                    AppendInstructionText(document, ref paragraph, segment.Text);
                }
                else if (segment.IsImageReference)
                {
                    paragraph.Inlines.Add(CreateInstructionReferenceInline(segment));
                }
            }

            ArtWorkspaceInstructionRichTextBox.Document = document;
            ArtWorkspaceInstructionRichTextBox.CaretPosition =
                document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward) ?? document.ContentEnd;
        }
        finally
        {
            _synchronizingInstructionEditor = false;
        }
    }

    private void SynchronizeInstructionEditor()
    {
        if (_synchronizingInstructionEditor ||
            ArtWorkspaceInstructionRichTextBox is null ||
            DataContext is not ArtWorkspaceViewModel viewModel)
        {
            return;
        }

        _synchronizingInstructionEditor = true;
        try
        {
            var accepted = viewModel.ReplaceInstructionDraftSegments(ReadInstructionEditorSegments());
            if (!accepted)
            {
                _synchronizingInstructionEditor = false;
                RenderInstructionEditor();
            }
        }
        catch (ArgumentException)
        {
            _synchronizingInstructionEditor = false;
            RenderInstructionEditor();
        }
        finally
        {
            _synchronizingInstructionEditor = false;
        }
    }

    private IReadOnlyList<ArtWorkspaceInstructionDraftSegment> ReadInstructionEditorSegments()
    {
        var result = new List<ArtWorkspaceInstructionDraftSegment>();
        var firstParagraph = true;
        foreach (var paragraph in ArtWorkspaceInstructionRichTextBox.Document.Blocks.OfType<Paragraph>())
        {
            if (!firstParagraph)
            {
                result.Add(ArtWorkspaceInstructionDraftSegment.CreateText(Environment.NewLine));
            }

            ReadInstructionInlines(paragraph.Inlines, result);
            firstParagraph = false;
        }

        return result;
    }

    private static void ReadInstructionInlines(
        InlineCollection inlines,
        ICollection<ArtWorkspaceInstructionDraftSegment> result)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    result.Add(ArtWorkspaceInstructionDraftSegment.CreateText(run.Text));
                    break;
                case LineBreak:
                    result.Add(ArtWorkspaceInstructionDraftSegment.CreateText(Environment.NewLine));
                    break;
                case InlineUIContainer { Tag: ArtWorkspaceInstructionDraftSegment segment }:
                    result.Add(segment.DeepClone());
                    break;
                case Span span:
                    ReadInstructionInlines(span.Inlines, result);
                    break;
            }
        }
    }

    private static Paragraph CreateInstructionParagraph() => new() { Margin = new Thickness(0) };

    private Paragraph EnsureTrailingInstructionParagraph()
    {
        if (ArtWorkspaceInstructionRichTextBox.Document.Blocks.LastBlock is Paragraph paragraph)
        {
            return paragraph;
        }

        paragraph = CreateInstructionParagraph();
        ArtWorkspaceInstructionRichTextBox.Document.Blocks.Add(paragraph);
        return paragraph;
    }

    private static void AppendInstructionText(
        FlowDocument document,
        ref Paragraph paragraph,
        string text)
    {
        var parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length > 0)
            {
                paragraph.Inlines.Add(new Run(parts[index]));
            }

            if (index >= parts.Length - 1)
            {
                continue;
            }

            paragraph = CreateInstructionParagraph();
            document.Blocks.Add(paragraph);
        }
    }
}
