using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.Collections.ObjectModel;

namespace SpinningWheelLib.Controls
{

    public static class VisualTreeHelperExtensions 
{
    public static T GetParentOfType<T>(this DependencyObject element) where T : DependencyObject
    {
        try
        {
            if (element == null) return null;
            
            var parent = VisualTreeHelper.GetParent(element);
            
            while (parent != null && !(parent is T))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            
            return parent as T;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetParentOfType: {ex.Message}");
            throw;
        }
    }
}

    public interface IRibbonStateStorage : IDisposable
    {
        bool IsLoaded { get; }
        void Load();
        void Save();
        void SaveTemporary();
    }

    public class RibbonStateStorage : IRibbonStateStorage
    {
        private readonly RibbonControl ribbon;
        private bool isMinimized;
        private bool isSimplified;
        private double height;

        public bool IsLoaded { get; private set; }

        public RibbonStateStorage(RibbonControl ribbon)
        {
            try
            {
                this.ribbon = ribbon;
                this.height = ribbon.Height;
                this.isMinimized = ribbon.IsFolded;
                this.isSimplified = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in RibbonStateStorage constructor: {ex.Message}");
                throw;
            }
        }

        public void Load()
        {
            try
            {
                if (IsLoaded) return;
                
                ribbon.Height = height;
                ribbon.IsFolded = isMinimized;
                IsLoaded = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Load: {ex.Message}");
                throw;
            }
        }

        public void Save()
        {
            try
            {
                height = ribbon.Height;
                isMinimized = ribbon.IsFolded;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Save: {ex.Message}");
                throw;
            }
        }

        public void SaveTemporary()
        {
            try
            {
                Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in SaveTemporary: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Dispose: {ex.Message}");
                throw;
            }
        }
    }

    public partial class RibbonControl : UserControl
    {
        private IRibbonStateStorage ribbonStateStorage;
        public IRibbonStateStorage RibbonStateStorage
        {
            get
            {
                try
                {
                    return ribbonStateStorage ??= CreateRibbonStateStorage();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in RibbonStateStorage getter: {ex.Message}");
                    throw;
                }
            }
        }

        protected virtual IRibbonStateStorage CreateRibbonStateStorage()
        {
            try
            {
                return new RibbonStateStorage(this);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in CreateRibbonStateStorage: {ex.Message}");
                throw;
            }
        }

        public static readonly DependencyProperty IsFoldedProperty =
            DependencyProperty.Register("IsFolded", typeof(bool), typeof(RibbonControl),
                new PropertyMetadata(true, OnIsFoldedChanged));

        public static readonly DependencyProperty QuickAccessToolbarContentProperty =
            DependencyProperty.Register("QuickAccessToolbarContent", typeof(object), typeof(RibbonControl));

        public static readonly DependencyProperty MenuContentProperty =
            DependencyProperty.Register("MenuContent", typeof(object), typeof(RibbonControl));

        public static readonly DependencyProperty SelectedTabIndexProperty =
            DependencyProperty.Register("SelectedTabIndex", typeof(int), typeof(RibbonControl),
                new PropertyMetadata(0, OnSelectedTabIndexChanged));

        public bool IsFolded
        {
            get
            {
                try
                {
                    return (bool)GetValue(IsFoldedProperty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in IsFolded getter: {ex.Message}");
                    throw;
                }
            }
            set
            {
                try
                {
                    SetValue(IsFoldedProperty, value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in IsFolded setter: {ex.Message}");
                    throw;
                }
            }
        }

        public object QuickAccessToolbarContent
        {
            get
            {
                try
                {
                    return GetValue(QuickAccessToolbarContentProperty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in QuickAccessToolbarContent getter: {ex.Message}");
                    throw;
                }
            }
            set
            {
                try
                {
                    SetValue(QuickAccessToolbarContentProperty, value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in QuickAccessToolbarContent setter: {ex.Message}");
                    throw;
                }
            }
        }

        public object MenuContent
        {
            get
            {
                try
                {
                    return GetValue(MenuContentProperty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in MenuContent getter: {ex.Message}");
                    throw;
                }
            }
            set
            {
                try
                {
                    SetValue(MenuContentProperty, value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in MenuContent setter: {ex.Message}");
                    throw;
                }
            }
        }

        public int SelectedTabIndex
        {
            get
            {
                try
                {
                    return (int)GetValue(SelectedTabIndexProperty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in SelectedTabIndex getter: {ex.Message}");
                    throw;
                }
            }
            set
            {
                try
                {
                    SetValue(SelectedTabIndexProperty, value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in SelectedTabIndex setter: {ex.Message}");
                    throw;
                }
            }
        }

        public ObservableCollection<TabItem> Tabs { get; } = new ObservableCollection<TabItem>();

        public event EventHandler StateChanged;

        public RibbonControl()
        {
            try
            {
                InitializeComponent();
                
                this.Loaded += OnLoaded;
                this.Unloaded += OnUnloaded;
                
                PART_TabControl.SelectionChanged += OnTabSelectionChanged;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in RibbonControl constructor: {ex.Message}");
                throw;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadInitialState();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnLoaded: {ex.Message}");
                throw;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RibbonStateStorage.Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnUnloaded: {ex.Message}");
                throw;
            }
        }

        private void LoadInitialState()
        {
            try
            {
                if (RibbonStateStorage.IsLoaded) return;
                RibbonStateStorage.Load();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in LoadInitialState: {ex.Message}");
                throw;
            }
        }

        private static void OnIsFoldedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            try
            {
                var ribbon = (RibbonControl)d;
                ribbon.UpdateRibbonHeight();
                ribbon.StateChanged?.Invoke(ribbon, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnIsFoldedChanged: {ex.Message}");
                throw;
            }
        }

        private static void OnSelectedTabIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            try
            {
                var ribbon = (RibbonControl)d;
                ribbon.PART_TabControl.SelectedIndex = (int)e.NewValue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnSelectedTabIndexChanged: {ex.Message}");
                throw;
            }
        }

        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (!IsFolded)
                {
                    UpdateRibbonHeight();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnTabSelectionChanged: {ex.Message}");
                throw;
            }
        }

        private void UpdateRibbonHeight()
{
    var parentTitleBar = this.Parent as CustomLonghornTitleBar;
    if (parentTitleBar != null)
    {
        var targetHeight = IsFolded ? 74 : 180;
        
        var window = Window.GetWindow(parentTitleBar);
        if (window != null)
        {
            window.InvalidateVisual();
            window.UpdateLayout();
        }

        var animation = new DoubleAnimation
        {
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(167),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        parentTitleBar.Height = targetHeight;
        parentTitleBar.BeginAnimation(HeightProperty, animation);
        parentTitleBar.UpdateLayout();
        
        RibbonStateStorage.SaveTemporary();
    }
}


        private void OnCollapseButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                IsFolded = !IsFolded;
                UpdateRibbonHeight();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnCollapseButtonClick: {ex.Message}");
                throw;
            }
        }

        public void AddTab(TabItem tab)
        {
            try
            {
                PART_TabControl.Items.Add(tab);
                Tabs.Add(tab);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in AddTab: {ex.Message}");
                throw;
            }
        }

        public void RemoveTab(TabItem tab)
        {
            try
            {
                PART_TabControl.Items.Remove(tab);
                Tabs.Remove(tab);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in RemoveTab: {ex.Message}");
                throw;
            }
        }

        public void ClearTabs()
        {
            try
            {
                PART_TabControl.Items.Clear();
                Tabs.Clear();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ClearTabs: {ex.Message}");
                throw;
            }
        }
    }
}
