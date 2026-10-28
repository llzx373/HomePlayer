using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HomePlayer;

public sealed partial class PagerBar : UserControl
{
    private int _total;
    private int _page = 1;
    private int _pageSize = PagedView.DefaultPageSize;
    private bool _updating;

    public PagerBar()
    {
        InitializeComponent();
        foreach (var size in PagedView.PageSizes)
            PageSizeBox.Items.Add(size);
        PageSizeBox.SelectedItem = _pageSize;
        Update(0, 1, _pageSize);
    }

    public event EventHandler<int>? PageRequested;
    public event EventHandler<int>? PageSizeChanged;

    public int Page => _page;

    public int PageSize => _pageSize;

    public void Update(int total, int page, int pageSize)
    {
        _updating = true;
        try
        {
            _total = Math.Max(0, total);
            _pageSize = pageSize < 1 ? PagedView.DefaultPageSize : pageSize;
            _page = PagedView.ClampPage(page, _total, _pageSize);
            if (!Equals(PageSizeBox.SelectedItem, _pageSize))
                PageSizeBox.SelectedItem = _pageSize;
            TotalText.Text = $"共 {_total:N0} 项";
            if (JumpBox.FocusState == FocusState.Unfocused)
                JumpBox.Text = _page.ToString();
            RebuildPages();
            var pages = PagedView.TotalPages(_total, _pageSize);
            Visibility = _total == 0 || pages <= 1 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _updating = false;
        }
    }

    private void RebuildPages()
    {
        PageItems.Children.Clear();
        var pages = PagedView.TotalPages(_total, _pageSize);
        foreach (var entry in PagedView.PageWindow(_page, pages))
        {
            if (entry is int number)
            {
                var button = CreatePageButton(number);
                button.Click += (_, _) => RequestPage(number);
                PageItems.Children.Add(button);
            }
            else
            {
                var button = CreatePageButton(0);
                button.Content = "…";
                button.Click += (_, _) => EditEllipsis(button);
                PageItems.Children.Add(button);
            }
        }
    }

    private Button CreatePageButton(int page)
    {
        var button = new Button
        {
            Content = (page == 0 ? "…" : page.ToString()),
            MinWidth = 32,
            Height = 28,
            Padding = new Thickness(6, 0, 6, 0)
        };
        if (page != 0 && page == _page)
        {
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style))
                button.Style = (Style)style;
            button.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }

        return button;
    }

    private void EditEllipsis(Button button)
    {
        var index = PageItems.Children.IndexOf(button);
        if (index < 0)
            return;
        var box = new TextBox
        {
            Width = 56,
            Height = 28,
            Padding = new Thickness(6, 4, 6, 4)
        };
        PageItems.Children.RemoveAt(index);
        PageItems.Children.Insert(index, box);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter)
                return;
            e.Handled = true;
            CommitJump(box.Text);
        };
        box.LostFocus += (_, _) =>
        {
            if (PageItems.Children.Contains(box))
                CommitJump(box.Text);
        };
    }

    private void RequestPage(int page)
    {
        var pages = PagedView.TotalPages(_total, _pageSize);
        page = Math.Clamp(page, 1, pages);
        if (page == _page)
        {
            Update(_total, _page, _pageSize);
            return;
        }

        PageRequested?.Invoke(this, page);
    }

    private void CommitJump(string? text)
    {
        var pages = PagedView.TotalPages(_total, _pageSize);
        if (!int.TryParse(text, out var page))
        {
            Update(_total, _page, _pageSize);
            return;
        }

        page = Math.Clamp(page, 1, pages);
        if (page == _page)
        {
            Update(_total, _page, _pageSize);
            return;
        }

        PageRequested?.Invoke(this, page);
    }

    private void First_Click(object sender, RoutedEventArgs e) => RequestPage(1);

    private void Previous_Click(object sender, RoutedEventArgs e) => RequestPage(_page - 1);

    private void Next_Click(object sender, RoutedEventArgs e) => RequestPage(_page + 1);

    private void Last_Click(object sender, RoutedEventArgs e) =>
        RequestPage(PagedView.TotalPages(_total, _pageSize));

    private void PageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || PageSizeBox.SelectedItem is not int size || size == _pageSize)
            return;
        PageSizeChanged?.Invoke(this, size);
    }

    private void Jump_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        CommitJump(JumpBox.Text);
    }

    private void Jump_LostFocus(object sender, RoutedEventArgs e) => CommitJump(JumpBox.Text);
}
