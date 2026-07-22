using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LookupAnythingMobileSearch.Framework;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace LookupAnythingMobileSearch.UI
{
    public class MobileSearchMenu : IClickableMenu
    {
        // Layout constants
        private const int PADDING = 16;
        private const int SEARCH_BOX_HEIGHT = 52;
        private const int SORT_BUTTON_WIDTH = 84;
        private const int CATEGORY_HEIGHT = 44;
        private const int SUBCATEGORY_HEIGHT = 30;
        private const int ITEM_HEIGHT = 80;
        private const int GROUP_HEADER_HEIGHT = 28;
        private const int SCROLLBAR_WIDTH = 12;
        private const float TAP_THRESHOLD = 20f;
        private const int BATCH_SIZE = 50;
        private const int STAR_SIZE = 28;

        private enum SortMode { NameAsc, NameDesc, ByCategory, ByMod }

        private class CategoryButton
        {
            public Rectangle Bounds { get; set; }
            public string Category { get; set; } = "";
            public bool IsSelected { get; set; }
        }

        // A visible row is either a group header (string) or an actual
        // result (SubjectWrapper) - kept as one flat list so scrolling math
        // stays simple (each row still has one height, just two heights).
        private abstract class Row { public abstract int Height { get; } }
        private class HeaderRow : Row { public string Text = ""; public override int Height => GROUP_HEADER_HEIGHT; }
        private class ItemRow : Row { public SubjectWrapper Subject = null!; public override int Height => ITEM_HEIGHT; }

        private readonly bool _isAndroid = (int)Constants.TargetPlatform == 0;
        private List<object> _rawSubjects;
        private readonly List<SubjectWrapper> _wrapped = new();
        private List<Row> _rows = new();
        private readonly Action<object> _onSelect;
        private readonly Func<List<object>>? _monsterProvider;
        private readonly Func<List<object>>? _animalProvider;
        private readonly PersistenceManager? _persistence;
        private readonly Action? _onExplicitClose;
        private bool _monstersLoaded;
        private bool _animalsLoaded;

        private int _wrapIndex;
        private bool _fullyLoaded;
        private bool _needsFilter = true;

        private readonly List<CategoryButton> _categories = new();
        private string _currentCategory = "All";
        private readonly List<CategoryButton> _subCategories = new();
        private readonly HashSet<string> _keptSubCategories = new();
        private int _subCategoryScrollX;
        private int _subCategoryMaxScrollX;
        private int _subCategoryTrackWidth;
        private int _subCategoryVisibleWidth;
        private bool _draggingSubCategories;
        private int _subCategoryDragStartX;
        private int _subCategoryDragStartScrollX;
        private Rectangle _subCategoryLeftArrowBounds;
        private Rectangle _subCategoryRightArrowBounds;
        private string _currentSubCategory = "All";
        private SortMode _sortMode = SortMode.NameAsc;

        private TextBox _searchBox = null!;
        private Rectangle _searchBoxBounds;
        private Rectangle _sortButtonBounds;
        private ClickableTextureComponent _searchIcon = null!;
        private ClickableTextureComponent _clearButton = null!;
        private ClickableTextureComponent _closeButton = null!;
        private string _lastSearch = "";

        private MethodInfo? _showKeyboard;
        private MethodInfo? _hideKeyboard;
        private bool _searchExplicit;

        private float _scroll;
        private float _maxScroll;
        private bool _dragging;
        private Vector2 _dragStart;
        private Vector2 _lastDrag;
        private float _dragDist;

        private Rectangle _categoryArea;
        private Rectangle _subCategoryArea;
        private Rectangle _resultsArea;
        private Rectangle _scrollbarArea;
        private Rectangle _scrollUpButtonBounds;
        private Rectangle _scrollDownButtonBounds;

        // Pseudo-categories that pull from persisted lists instead of
        // "everything that matches this SubjectWrapper property".
        private const string CAT_FAVORITES = "Favorites";
        private const string CAT_RECENT = "Recent";

        public MobileSearchMenu(IEnumerable<object> subjects, Action<object> onSelect,
                Func<List<object>>? monsterProvider = null, PersistenceManager? persistence = null,
                Action? onExplicitClose = null, Func<List<object>>? animalProvider = null)
            : base(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height, false)
        {
            _onSelect = onSelect;
            _rawSubjects = subjects.ToList();
            _monsterProvider = monsterProvider;
            _animalProvider = animalProvider;
            _persistence = persistence;
            _onExplicitClose = onExplicitClose;

            if (_isAndroid) CacheKeyboardMethods();
            CalculateLayout();
            CreateComponents();
        }

        private void CacheKeyboardMethods()
        {
            var t = typeof(TextBox);
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _showKeyboard = t.GetMethod("ShowAndroidKeyboard", flags);
            _hideKeyboard = t.GetMethod("HideAndroidKeyboard", flags)
                         ?? t.GetMethod("HideStatusBar", flags);
        }

        private const int CATEGORY_ROWS = 2;
        private const int SUBCATEGORY_ROWS = 1;
        private const int SUBCATEGORY_ARROW_WIDTH = 22;
        private const int ROW_GAP = 4;
        private const int MAX_SUBCATEGORIES = 6; // beyond this, extras fold into "Other"
        private const int SCROLL_ARROW_SIZE = 40;

        private int CategoryAreaHeight => CATEGORY_HEIGHT * CATEGORY_ROWS + ROW_GAP * (CATEGORY_ROWS - 1);
        private int SubCategoryAreaHeight => SUBCATEGORY_HEIGHT * SUBCATEGORY_ROWS + ROW_GAP * (SUBCATEGORY_ROWS - 1);

        private void CalculateLayout()
        {
            int margin = Math.Min(24, Game1.uiViewport.Width / 25);
            xPositionOnScreen = margin;
            yPositionOnScreen = margin;
            width = Game1.uiViewport.Width - margin * 2;
            height = Game1.uiViewport.Height - margin * 2;

            int cx = xPositionOnScreen + PADDING;
            int cw = width - PADDING * 2 - SCROLL_ARROW_SIZE - 8;
            int cy = yPositionOnScreen + PADDING + 40;

            _searchBoxBounds = new Rectangle(cx + 20, cy, cw - 56 - SORT_BUTTON_WIDTH - 8, SEARCH_BOX_HEIGHT);
            _sortButtonBounds = new Rectangle(_searchBoxBounds.Right + 8, cy, SORT_BUTTON_WIDTH, SEARCH_BOX_HEIGHT);
            cy += SEARCH_BOX_HEIGHT + 12;
            _categoryArea = new Rectangle(cx, cy, cw, CategoryAreaHeight);
            cy += CategoryAreaHeight + 6;
            _subCategoryArea = new Rectangle(cx, cy, cw, SubCategoryAreaHeight);
            _subCategoryLeftArrowBounds = new Rectangle(_subCategoryArea.X, _subCategoryArea.Y, SUBCATEGORY_ARROW_WIDTH, SUBCATEGORY_HEIGHT);
            _subCategoryRightArrowBounds = new Rectangle(_subCategoryArea.Right - SUBCATEGORY_ARROW_WIDTH, _subCategoryArea.Y, SUBCATEGORY_ARROW_WIDTH, SUBCATEGORY_HEIGHT);
            cy += SubCategoryAreaHeight + 10;
            _resultsArea = new Rectangle(cx + 14, cy, cw - 20, yPositionOnScreen + height - cy - PADDING - 24);

            // Arrows + scrollbar grouped into ONE column stack (up arrow,
            // then the scrollbar track filling the middle, then down
            // arrow) instead of being split into separate columns, so
            // they read as a single scroll control.
            int stackX = _resultsArea.Right + 8;
            _scrollUpButtonBounds = new Rectangle(stackX, _resultsArea.Y, SCROLL_ARROW_SIZE, SCROLL_ARROW_SIZE);
            _scrollDownButtonBounds = new Rectangle(stackX, _resultsArea.Bottom - SCROLL_ARROW_SIZE, SCROLL_ARROW_SIZE, SCROLL_ARROW_SIZE);
            _scrollbarArea = new Rectangle(
                    stackX + (SCROLL_ARROW_SIZE - SCROLLBAR_WIDTH) / 2,
                    _scrollUpButtonBounds.Bottom + 4,
                    SCROLLBAR_WIDTH,
                    _scrollDownButtonBounds.Y - _scrollUpButtonBounds.Bottom - 8);
        }

        // Recomputes layout and re-lays-out existing buttons against the
        // current viewport - call this before bringing a previously-built
        // menu instance back on screen (e.g. when restoring it after the
        // player closes a detail page), since the viewport may have
        // changed since this instance was first created and stale
        // absolute-pixel bounds would draw it undersized/mispositioned and
        // break click hit-testing.
        public void RefreshLayout()
        {
            CalculateLayout();
            _searchBox.X = _searchBoxBounds.X + 40;
            _searchBox.Y = _searchBoxBounds.Y + 4;
            _searchBox.Width = _searchBoxBounds.Width - 50;
            _searchBox.Height = _searchBoxBounds.Height;
            _searchIcon.bounds = new Rectangle(_searchBoxBounds.X + 8, _searchBoxBounds.Y + (_searchBoxBounds.Height - 26) / 2, 26, 26);
            // Sits INSIDE the right edge of the search box itself (not
            // to the right of it) - it used to be positioned at
            // "_searchBoxBounds.Right + 8", but that's the exact same spot
            // the sort button now occupies, so the two were fully
            // overlapping and the clear button was effectively unusable.
            const int clearSize = 36;
            _clearButton.bounds = new Rectangle(_searchBoxBounds.Right - clearSize - 6, _searchBoxBounds.Y + (_searchBoxBounds.Height - clearSize) / 2, clearSize, clearSize);
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 56 - 8, yPositionOnScreen + 8, 56, 56);
            var catLabels = _categories.Select(c => c.Category).ToList();
            RebuildCategoryButtons(catLabels);
            RebuildSubCategoryButtons();
            _needsFilter = true;
        }

        private void CreateComponents()
        {
            _searchBox = new TextBox(
                Game1.content.Load<Texture2D>("LooseSprites\\textBox"),
                null, Game1.smallFont, Game1.textColor)
            {
                X = _searchBoxBounds.X + 40,
                Y = _searchBoxBounds.Y + 4,
                Width = _searchBoxBounds.Width - 50,
                Height = _searchBoxBounds.Height,
                Text = ""
            };

            _searchIcon = new ClickableTextureComponent(
                new Rectangle(_searchBoxBounds.X + 8, _searchBoxBounds.Y + (_searchBoxBounds.Height - 26) / 2, 26, 26),
                Game1.mouseCursors, new Rectangle(80, 0, 13, 13), 2f);

            _clearButton = new ClickableTextureComponent(
                new Rectangle(_searchBoxBounds.Right - 42, _searchBoxBounds.Y + (_searchBoxBounds.Height - 36) / 2, 36, 36),
                Game1.mouseCursors, new Rectangle(322, 498, 12, 12), 3f);

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 56 - 8, yPositionOnScreen + 8, 56, 56),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 4.667f);

            RebuildCategoryButtons(new List<string> { "All", "Items", "NPCs", "Monsters", "Buildings", CAT_FAVORITES, CAT_RECENT });
        }

        private void RebuildCategoryButtons(List<string> cats)
        {
            _categories.Clear();
            var rows = SplitIntoRows(cats, CATEGORY_ROWS);
            for (int r = 0; r < rows.Count; r++)
            {
                var rowCats = rows[r];
                if (rowCats.Count == 0) continue;
                int btnW = (_categoryArea.Width - (rowCats.Count - 1) * 6) / rowCats.Count;
                int x = _categoryArea.X;
                int y = _categoryArea.Y + r * (CATEGORY_HEIGHT + ROW_GAP);
                foreach (var cat in rowCats)
                {
                    _categories.Add(new CategoryButton
                    {
                        Bounds = new Rectangle(x, y, btnW, CATEGORY_HEIGHT),
                        Category = cat,
                        IsSelected = cat == _currentCategory
                    });
                    x += btnW + 6;
                }
            }
        }

        private void RebuildSubCategoryButtons()
        {
            _subCategories.Clear();
            if (_currentCategory == "All" || _currentCategory == CAT_FAVORITES || _currentCategory == CAT_RECENT)
                return;

            var subCounts = _wrapped
                    .Where(s => s.GetCategory() == _currentCategory)
                    .Select(s => s.GetSubCategory())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .GroupBy(s => s)
                    .ToDictionary(g => g.Key, g => g.Count());
            if (subCounts.Count == 0) return;

            // Sub-categories now come from a small, fixed classification
            // (Weapon/Ring/Boots/Hat/Clothing/Furniture/Tool/Other for
            // Items, similar small fixed sets elsewhere) rather than a
            // wide-open raw string, so there's no need to cap/fold by
            // frequency anymore - that used to squeeze out legitimately
            // distinct categories with fewer members (Boots, Tool) in
            // favor of whichever happened to have more items, dumping
            // them into "Other" even though they were classified
            // correctly. Just show a tab for every category that has at
            // least one member.
            var kept = subCounts.Keys.OrderBy(s => s).ToList();
            bool hasOverflow = false;
            _keptSubCategories.Clear();
            foreach (var k in kept) _keptSubCategories.Add(k);

            var list = new List<string> { "All" };
            list.AddRange(kept);
            if (hasOverflow) list.Add("Other");
            if (!list.Contains(_currentSubCategory)) _currentSubCategory = "All";

            // Single row, natural width per tab (based on its own text),
            // laid out left-to-right with no wrapping - horizontal drag
            // and the arrow buttons scroll through it. This replaces the
            // old fixed-width multi-row grid, which ran out of room once
            // there were more than about a dozen sub-categories.
            int trackX = _subCategoryArea.X + SUBCATEGORY_ARROW_WIDTH + 4;
            int x = trackX;
            const int tabPaddingX = 14;
            const int tabGap = 4;
            foreach (var sub in list)
            {
                var textSize = Game1.tinyFont.MeasureString(sub);
                int btnW = (int)textSize.X + tabPaddingX * 2;
                _subCategories.Add(new CategoryButton
                {
                    Bounds = new Rectangle(x, _subCategoryArea.Y, btnW, SUBCATEGORY_HEIGHT),
                    Category = sub,
                    IsSelected = sub == _currentSubCategory
                });
                x += btnW + tabGap;
            }
            _subCategoryTrackWidth = x - tabGap - trackX;
            _subCategoryVisibleWidth = _subCategoryArea.Width - (SUBCATEGORY_ARROW_WIDTH + 4) * 2;
            _subCategoryMaxScrollX = Math.Max(0, _subCategoryTrackWidth - _subCategoryVisibleWidth);
            _subCategoryScrollX = Math.Clamp(_subCategoryScrollX, 0, _subCategoryMaxScrollX);
        }

        // Splits a label list evenly across up to maxRows rows (first rows
        // get the extra items when it doesn't divide evenly), so a long
        // tab list wraps to multiple readable rows instead of squeezing
        // every tab into one row so narrow the text can't render.
        private static List<List<string>> SplitIntoRows(List<string> items, int maxRows)
        {
            var result = new List<List<string>>();
            if (items.Count == 0) { for (int i = 0; i < maxRows; i++) result.Add(new List<string>()); return result; }
            int perRow = (int)Math.Ceiling(items.Count / (double)maxRows);
            for (int i = 0; i < items.Count; i += perRow)
                result.Add(items.Skip(i).Take(perRow).ToList());
            while (result.Count < maxRows) result.Add(new List<string>());
            return result;
        }

        // ──────────────── Input ────────────────

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y))
            {
                _onExplicitClose?.Invoke();
                exitThisMenu();
                return;
            }

            if (_sortButtonBounds.Contains(x, y))
            {
                _sortMode = (SortMode)(((int)_sortMode + 1) % 4);
                _needsFilter = true;
                if (playSound) Game1.playSound("smallSelect");
                return;
            }

            if (!string.IsNullOrEmpty(_searchBox.Text) && _clearButton.bounds.Contains(x, y))
            {
                _searchBox.Text = "";
                _needsFilter = true;
                Game1.playSound("smallSelect");
                return;
            }

            if (_searchBoxBounds.Contains(x, y))
            {
                if (!_searchBox.Selected || !_searchExplicit)
                {
                    if (playSound) Game1.playSound("smallSelect");
                    SelectSearch(true);
                }
                return;
            }

            if (_searchBox.Selected) DeselectSearch();

            foreach (var btn in _categories)
            {
                if (!btn.Bounds.Contains(x, y)) continue;
                if (_currentCategory != btn.Category)
                {
                    _currentCategory = btn.Category;
                    _currentSubCategory = "All";
                    foreach (var b2 in _categories) b2.IsSelected = b2.Category == _currentCategory;
                    if (btn.Category == "Monsters") EnsureMonstersLoaded();
                    if (btn.Category == "Animals") EnsureAnimalsLoaded();
                    RebuildSubCategoryButtons();
                    _needsFilter = true;
                    if (playSound) Game1.playSound("smallSelect");
                }
                return;
            }

            if (_subCategoryLeftArrowBounds.Contains(x, y))
            {
                _subCategoryScrollX = Math.Max(0, _subCategoryScrollX - 120);
                if (playSound) Game1.playSound("smallSelect");
                return;
            }
            if (_subCategoryRightArrowBounds.Contains(x, y))
            {
                _subCategoryScrollX = Math.Min(_subCategoryMaxScrollX, _subCategoryScrollX + 120);
                if (playSound) Game1.playSound("smallSelect");
                return;
            }

            foreach (var btn in _subCategories)
            {
                // Stored bounds are in un-scrolled "track" space; shift the
                // click position by the current scroll offset to compare
                // against them (equivalent to un-scrolling the click).
                if (!btn.Bounds.Contains(x + _subCategoryScrollX, y)) continue;
                if (_currentSubCategory != btn.Category)
                {
                    _currentSubCategory = btn.Category;
                    foreach (var b2 in _subCategories) b2.IsSelected = b2.Category == _currentSubCategory;
                    _needsFilter = true;
                    if (playSound) Game1.playSound("smallSelect");
                }
                return;
            }

            if (_subCategoryArea.Contains(x, y) && _subCategoryMaxScrollX > 0)
            {
                _draggingSubCategories = true;
                _subCategoryDragStartX = x;
                _subCategoryDragStartScrollX = _subCategoryScrollX;
            }

            if (_scrollUpButtonBounds.Contains(x, y))
            {
                _heldArrow = -1;
                _arrowHeldTime = 0f;
                DoArrowScroll(-1);
                if (playSound) Game1.playSound("shwip");
                return;
            }
            if (_scrollDownButtonBounds.Contains(x, y))
            {
                _heldArrow = 1;
                _arrowHeldTime = 0f;
                DoArrowScroll(1);
                if (playSound) Game1.playSound("shwip");
                return;
            }

            if (_scrollbarArea.Contains(x, y) && _maxScroll > 0)
            {
                _draggingScrollbar = true;
                SetScrollFromScrollbarY(y);
                return;
            }

            if (_resultsArea.Contains(x, y))
            {
                // Star tap zone check happens on release (tap, not drag) -
                // just start tracking the potential drag/tap here.
                _dragging = true;
                _dragStart = _lastDrag = new Vector2(x, y);
                _dragDist = 0;
            }
        }

        private const int ARROW_SCROLL_AMOUNT = 200;
        private int _heldArrow; // -1 = up, 1 = down, 0 = none
        private float _arrowHeldTime;
        private bool _draggingScrollbar;

        private void DoArrowScroll(int direction)
        {
            _scroll = MathHelper.Clamp(_scroll + direction * ARROW_SCROLL_AMOUNT, 0, _maxScroll);
        }

        private void SetScrollFromScrollbarY(int y)
        {
            float ratio = MathHelper.Clamp((y - _scrollbarArea.Y) / (float)_scrollbarArea.Height, 0f, 1f);
            _scroll = ratio * _maxScroll;
        }

        public override void leftClickHeld(int x, int y)
        {
            if (_draggingSubCategories)
            {
                int delta = _subCategoryDragStartX - x;
                _subCategoryScrollX = Math.Clamp(_subCategoryDragStartScrollX + delta, 0, _subCategoryMaxScrollX);
                return;
            }
            if (_draggingScrollbar)
            {
                SetScrollFromScrollbarY(y);
                return;
            }
            if (!_dragging) return;
            var cur = new Vector2(x, y);
            float delta2 = _lastDrag.Y - y;
            _dragDist += Vector2.Distance(_lastDrag, cur);
            _scroll = MathHelper.Clamp(_scroll + delta2, 0, _maxScroll);
            _lastDrag = cur;
        }

        public override void releaseLeftClick(int x, int y)
        {
            if (_dragging && _dragDist < TAP_THRESHOLD)
                TrySelect((int)_dragStart.X, (int)_dragStart.Y);
            _dragging = false;
            _dragDist = 0;
            _heldArrow = 0;
            _draggingScrollbar = false;
            _draggingSubCategories = false;
        }

        public override void receiveScrollWheelAction(int direction)
        {
            _scroll = MathHelper.Clamp(_scroll + (direction > 0 ? -160f : 160f), 0, _maxScroll);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                if (_searchBox.Selected && !string.IsNullOrEmpty(_searchBox.Text))
                { _searchBox.Text = ""; DeselectSearch(); }
                else if (_searchBox.Selected) DeselectSearch();
                else { _onExplicitClose?.Invoke(); exitThisMenu(); }
            }
        }

        // ──────────────── Lazy monster loading ────────────────

        private void EnsureMonstersLoaded()
        {
            if (_monstersLoaded || _monsterProvider == null) return;
            _monstersLoaded = true;
            List<object> monsters = _monsterProvider.Invoke() ?? new List<object>();
            if (monsters.Count == 0) return;
            _rawSubjects = _rawSubjects.Concat(monsters).ToList();
            _fullyLoaded = false;
        }

        private void EnsureAnimalsLoaded()
        {
            if (_animalsLoaded || _animalProvider == null) return;
            _animalsLoaded = true;
            List<object> animals = _animalProvider.Invoke() ?? new List<object>();
            if (animals.Count == 0) return;
            _rawSubjects = _rawSubjects.Concat(animals).ToList();
            _fullyLoaded = false;
        }

        private void TrySelect(int x, int y)
        {
            if (!_resultsArea.Contains(x, y)) return;

            // Star tap zone: right-hand edge of each item row.
            var (row, rowBounds) = FindRowAt(y);
            if (row is ItemRow itemRow && _persistence != null)
            {
                var starRect = StarBounds(rowBounds);
                if (starRect.Contains(x, y))
                {
                    _persistence.ToggleFavorite(itemRow.Subject.InternalName);
                    Game1.playSound("coin");
                    if (_currentCategory == CAT_FAVORITES) _needsFilter = true;
                    return;
                }
            }

            if (row is ItemRow ir)
            {
                _persistence?.RecordViewed(ir.Subject.InternalName);
                Game1.playSound("select");
                _onSelect?.Invoke(ir.Subject.RawSubject);
            }
        }

        private (Row? row, Rectangle bounds) FindRowAt(int y)
        {
            float acc = -_scroll;
            foreach (var row in _rows)
            {
                var bounds = new Rectangle(_resultsArea.X, _resultsArea.Y + (int)acc, _resultsArea.Width, row.Height);
                if (y >= bounds.Y && y < bounds.Bottom) return (row, bounds);
                acc += row.Height;
            }
            return (null, Rectangle.Empty);
        }

        private static Rectangle StarBounds(Rectangle rowBounds)
        {
            return new Rectangle(rowBounds.Right - STAR_SIZE - 10, rowBounds.Y + (rowBounds.Height - STAR_SIZE) / 2, STAR_SIZE, STAR_SIZE);
        }

        // ──────────────── Search box ────────────────

        private void SelectSearch(bool explicit_)
        {
            _searchBox.Selected = true;
            _searchExplicit = explicit_;
            if (_isAndroid && _showKeyboard != null)
            {
                try { _showKeyboard.Invoke(_searchBox, null); }
                catch { Game1.showTextEntry(_searchBox); }
            }
        }

        private void DeselectSearch()
        {
            Game1.closeTextEntry();
            _searchBox.Selected = false;
            _searchExplicit = false;
            if (_isAndroid) try { _hideKeyboard?.Invoke(_searchBox, null); } catch { }
        }

        // ──────────────── Update ────────────────

        public override void update(GameTime time)
        {
            base.update(time);
            if (!_fullyLoaded) LazyLoad();

            string text = _searchBox.Text ?? "";
            if (_lastSearch != text) { _lastSearch = text; _needsFilter = true; }
            if (_searchExplicit && !_searchBox.Selected) DeselectSearch();
            if (_needsFilter) ApplyFilter();

            // Holding an up/down arrow button keeps scrolling: a short
            // initial delay (so a single tap doesn't double-scroll), then
            // repeats quickly while held.
            if (_heldArrow != 0)
            {
                const float initialDelay = 0.35f;
                const float repeatInterval = 0.06f;
                float dt = (float)time.ElapsedGameTime.TotalSeconds;
                float prevTime = _arrowHeldTime;
                _arrowHeldTime += dt;
                if (prevTime < initialDelay && _arrowHeldTime >= initialDelay)
                {
                    DoArrowScroll(_heldArrow);
                }
                else if (_arrowHeldTime >= initialDelay)
                {
                    float sinceRepeatStart = _arrowHeldTime - initialDelay;
                    float prevSinceRepeatStart = prevTime - initialDelay;
                    if ((int)(sinceRepeatStart / repeatInterval) > (int)(prevSinceRepeatStart / repeatInterval))
                    {
                        DoArrowScroll(_heldArrow);
                    }
                }
            }
        }

        private void LazyLoad()
        {
            int end = Math.Min(_wrapIndex + BATCH_SIZE, _rawSubjects.Count);
            for (int i = _wrapIndex; i < end; i++)
            {
                var w = SubjectWrapper.Create(_rawSubjects[i]);
                if (w != null) _wrapped.Add(w);
            }
            _wrapIndex = end;
            if (_wrapIndex >= _rawSubjects.Count)
            {
                _fullyLoaded = true;
                UpdateCategoryButtons();
                RebuildSubCategoryButtons();
            }
            _needsFilter = true;
        }

        private void UpdateCategoryButtons()
        {
            var cats = _wrapped.Select(s => s.GetCategory()).Distinct().ToList();
            if (_monsterProvider != null && !cats.Contains("Monsters")) cats.Add("Monsters");
            if (_animalProvider != null && !cats.Contains("Animals")) cats.Add("Animals");
            cats.Sort();

            var list = new List<string> { "All" };
            list.AddRange(cats);
            list.Add(CAT_FAVORITES);
            list.Add(CAT_RECENT);
            RebuildCategoryButtons(list);
        }

        // ──────────────── Filtering / sorting / grouping ────────────────

        private void ApplyFilter()
        {
            string q = (_searchBox.Text ?? "").Trim();
            string qLower = q.ToLowerInvariant();

            IEnumerable<SubjectWrapper> source;
            if (_currentCategory == CAT_FAVORITES)
            {
                var favs = _persistence?.Favorites ?? Array.Empty<string>() as IReadOnlyList<string>;
                var favSet = new HashSet<string>(favs);
                source = _wrapped.Where(s => favSet.Contains(s.InternalName));
            }
            else if (_currentCategory == CAT_RECENT)
            {
                var recent = _persistence?.RecentlyViewed ?? Array.Empty<string>() as IReadOnlyList<string>;
                var byId = _wrapped
                        .GroupBy(s => s.InternalName, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First());
                source = recent.Select(id => byId.TryGetValue(id, out var s) ? s : null).Where(s => s != null)!;
            }
            else
            {
                source = _wrapped.Where(s => _currentCategory == "All" || s.GetCategory() == _currentCategory);
                if (_currentSubCategory == "Other")
                    source = source.Where(s => !_keptSubCategories.Contains(s.GetSubCategory()));
                else if (_currentSubCategory != "All")
                    source = source.Where(s => s.GetSubCategory() == _currentSubCategory);
            }

            List<SubjectWrapper> filtered = source.Where(s => MatchesSearch(s, qLower)).ToList();

            // Real entries first, clones (no real lookup data) pushed to
            // the very end - within each group, apply the chosen sort.
            var real = filtered.Where(s => s.HasRealData()).ToList();
            var clones = filtered.Where(s => !s.HasRealData()).ToList();

            _rows = new List<Row>();
            bool grouping = _sortMode == SortMode.ByCategory || _sortMode == SortMode.ByMod;
            if (!string.IsNullOrEmpty(qLower))
            {
                // While actively searching, relevance beats any sort mode -
                // exact/prefix matches first, then alphabetical.
                real = real.OrderBy(s => !StartsWithMatch(s, qLower)).ThenBy(s => s.Name).ToList();
                AppendRows(real, false);
            }
            else if (grouping)
            {
                AppendGrouped(real, _sortMode == SortMode.ByMod);
            }
            else
            {
                real = SortPlain(real, _sortMode);
                AppendRows(real, false);
            }

            if (clones.Count > 0)
            {
                _rows.Add(new HeaderRow { Text = "\u2014 NO DATA \u2014" });
                clones = SortPlain(clones, _sortMode == SortMode.ByCategory || _sortMode == SortMode.ByMod ? SortMode.NameAsc : _sortMode);
                AppendRows(clones, true);
            }

            _scroll = 0;
            int total = _rows.Sum(r => r.Height);
            _maxScroll = Math.Max(0, total - _resultsArea.Height);
            _needsFilter = false;
        }

        private void AppendRows(List<SubjectWrapper> subjects, bool isCloneGroup)
        {
            foreach (var s in subjects) _rows.Add(new ItemRow { Subject = s });
        }

        private void AppendGrouped(List<SubjectWrapper> subjects, bool byMod)
        {
            var groups = subjects
                    .GroupBy(s => byMod ? s.ModGroupLabel() : s.GetCategory())
                    .OrderBy(g => g.Key);
            foreach (var g in groups)
            {
                _rows.Add(new HeaderRow { Text = "\u2014 " + g.Key.ToUpperInvariant() + " \u2014" });
                foreach (var s in g.OrderBy(s => s.Name)) _rows.Add(new ItemRow { Subject = s });
            }
        }

        private static List<SubjectWrapper> SortPlain(List<SubjectWrapper> list, SortMode mode)
        {
            return mode switch
            {
                SortMode.NameDesc => list.OrderByDescending(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }

        // Matches on translated name/description AND the internal
        // (English) name, so players can search by an English name they
        // remember from a wiki even while playing in Thai. Falls back to a
        // typo-tolerant check (allows ~1 edit per 4 characters) so small
        // mobile-keyboard typos still find the right entry.
        private static bool MatchesSearch(SubjectWrapper s, string qLower)
        {
            if (string.IsNullOrEmpty(qLower)) return true;
            if (s.Name.ToLowerInvariant().Contains(qLower)) return true;
            if (s.Description.ToLowerInvariant().Contains(qLower)) return true;
            if (s.InternalName.ToLowerInvariant().Contains(qLower)) return true;
            return FuzzyContains(s.Name.ToLowerInvariant(), qLower);
        }

        private static bool StartsWithMatch(SubjectWrapper s, string qLower)
            => s.Name.ToLowerInvariant().StartsWith(qLower) || s.InternalName.ToLowerInvariant().StartsWith(qLower);

        // Cheap typo tolerance: slide the query across the name and accept
        // a window if its edit distance to the query is small relative to
        // its length. Good enough for 1-2 typo/transposition mistakes on a
        // touch keyboard without needing a real fuzzy-search library.
        private static bool FuzzyContains(string haystack, string needle)
        {
            if (needle.Length < 3) return false; // too short to fuzzy-match meaningfully
            int maxDist = Math.Max(1, needle.Length / 4);
            int windowLen = needle.Length;
            for (int start = 0; start <= haystack.Length - Math.Max(1, windowLen - maxDist); start++)
            {
                int end = Math.Min(haystack.Length, start + windowLen + maxDist);
                string window = haystack[start..end];
                if (EditDistance(window, needle) <= maxDist) return true;
            }
            return false;
        }

        private static int EditDistance(string a, string b)
        {
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[a.Length, b.Length];
        }

        // ──────────────── Draw ────────────────

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.75f);

            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            string title = "Search Encyclopedia";
            float scale = 0.75f;
            var titleSize = Game1.dialogueFont.MeasureString(title) * scale;
            var titlePos = new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 16);
            b.DrawString(Game1.dialogueFont, title, titlePos + new Vector2(2, 2), Color.Black * 0.5f, 0, Vector2.Zero, scale, SpriteEffects.None, 1f);
            b.DrawString(Game1.dialogueFont, title, titlePos, Game1.textColor, 0, Vector2.Zero, scale, SpriteEffects.None, 1f);

            _closeButton.draw(b);
            DrawSearchBox(b);
            DrawSortButton(b);
            if (!string.IsNullOrEmpty(_searchBox.Text)) _clearButton.draw(b);
            DrawCategories(b);
            DrawSubCategories(b);

            b.Draw(Game1.staminaRect, _resultsArea, new Color(60, 45, 30) * 0.15f);
            DrawResults(b);
            DrawScrollbar(b);
            DrawScrollArrows(b);
            DrawCount(b);

            drawMouse(b);
        }

        private void DrawSearchBox(SpriteBatch b)
        {
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                _searchBoxBounds.X - 4, _searchBoxBounds.Y - 4,
                _searchBoxBounds.Width + 8, _searchBoxBounds.Height + 8, Color.White, drawShadow: false);
            b.Draw(Game1.staminaRect, _searchBoxBounds, Color.White);
            if (_searchBox.Selected)
                b.Draw(Game1.staminaRect, _searchBoxBounds, new Color(224, 169, 48) * 0.18f);
            _searchIcon.draw(b, Color.Gray, 1f);
            _searchBox.Draw(b, true);
            if (string.IsNullOrEmpty(_searchBox.Text) && !_searchBox.Selected)
                Utility.drawTextWithShadow(b, "Tap to search...", Game1.smallFont,
                    new Vector2(_searchBox.X + 15, _searchBox.Y + 4), Color.Gray);
        }

        private void DrawSortButton(SpriteBatch b)
        {
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                _sortButtonBounds.X, _sortButtonBounds.Y, _sortButtonBounds.Width, _sortButtonBounds.Height,
                new Color(216, 189, 142), drawShadow: false);
            string label = _sortMode switch
            {
                SortMode.NameAsc => "A-Z",
                SortMode.NameDesc => "Z-A",
                SortMode.ByCategory => "Type",
                SortMode.ByMod => "Mod",
                _ => "?",
            };
            var sz = Game1.smallFont.MeasureString(label);
            var pos = new Vector2(_sortButtonBounds.X + (_sortButtonBounds.Width - sz.X) / 2f, _sortButtonBounds.Y + (_sortButtonBounds.Height - sz.Y) / 2f);
            Utility.drawTextWithShadow(b, label, Game1.smallFont, pos, new Color(74, 47, 20));
        }

        private void DrawCategories(SpriteBatch b)
        {
            foreach (var btn in _categories)
            {
                var bgColor = btn.IsSelected ? new Color(244, 226, 184) : new Color(216, 189, 142);
                var textColor = btn.IsSelected ? new Color(74, 47, 20) : new Color(120, 90, 60);
                var bounds = btn.Bounds;
                if (btn.IsSelected)
                    bounds = new Rectangle(bounds.X, bounds.Y - 2, bounds.Width, bounds.Height + 2);
                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, bgColor, drawShadow: false);
                string label = Truncate(btn.Category, Game1.smallFont, bounds.Width - 12);
                var labelSize = Game1.smallFont.MeasureString(label);
                var pos = new Vector2(
                    bounds.X + (bounds.Width - labelSize.X) / 2f,
                    bounds.Y + (bounds.Height - labelSize.Y) / 2f);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, pos, textColor);
            }
        }

        private void DrawSubCategories(SpriteBatch b)
        {
            if (_subCategories.Count == 0) return;

            int trackLeft = _subCategoryLeftArrowBounds.Right + 4;
            int trackRight = _subCategoryRightArrowBounds.Left - 4;

            foreach (var btn in _subCategories)
            {
                int screenX = btn.Bounds.X - _subCategoryScrollX;
                if (screenX + btn.Bounds.Width < trackLeft || screenX > trackRight) continue; // fully off-screen

                var textColor = btn.IsSelected ? new Color(74, 47, 20) : new Color(140, 110, 80);
                string label = Truncate(btn.Category, Game1.tinyFont, btn.Bounds.Width - 6);
                var sz = Game1.tinyFont.MeasureString(label);
                var pos = new Vector2(screenX + (btn.Bounds.Width - sz.X) / 2f, btn.Bounds.Y + 2);
                Utility.drawTextWithShadow(b, label, Game1.tinyFont, pos, textColor);
                if (btn.IsSelected)
                    b.Draw(Game1.staminaRect, new Rectangle(screenX, btn.Bounds.Bottom - 3, btn.Bounds.Width, 3), new Color(122, 74, 43));
            }

            // Scroll arrows - only meaningfully clickable/visible when
            // there's something to scroll to in that direction.
            DrawSubCategoryArrow(b, _subCategoryLeftArrowBounds, left: true, _subCategoryScrollX > 0);
            DrawSubCategoryArrow(b, _subCategoryRightArrowBounds, left: false, _subCategoryScrollX < _subCategoryMaxScrollX);
        }

        private static void DrawSubCategoryArrow(SpriteBatch b, Rectangle bounds, bool left, bool enabled)
        {
            if (!enabled) return;
            var color = new Color(122, 74, 43);
            int cx = bounds.X + bounds.Width / 2;
            int cy = bounds.Y + bounds.Height / 2;
            int size = 5;
            for (int i = 0; i < size; i++)
            {
                int xOff = left ? size - i : i;
                b.Draw(Game1.staminaRect, new Rectangle(cx - size / 2 + xOff, cy - size + i, 2, 2), color);
                b.Draw(Game1.staminaRect, new Rectangle(cx - size / 2 + xOff, cy + size - i, 2, 2), color);
            }
        }

        private void DrawResults(SpriteBatch b)
        {
            if (_rows.Count == 0)
            {
                string msg = !_fullyLoaded ? "Loading..."
                        : (_currentCategory == CAT_FAVORITES ? "No favorites yet - tap the star on an entry"
                        : _currentCategory == CAT_RECENT ? "Nothing viewed yet"
                        : "No results found - try a shorter search or another tab");
                var sz = Game1.smallFont.MeasureString(msg);
                Utility.drawTextWithShadow(b, msg, Game1.smallFont,
                    new Vector2(_resultsArea.X + (_resultsArea.Width - sz.X) / 2f,
                                _resultsArea.Y + (_resultsArea.Height - sz.Y) / 2f), Color.Gray);
                return;
            }

            float acc = -_scroll;
            int idx = 0;
            foreach (var row in _rows)
            {
                int iy = _resultsArea.Y + (int)acc;
                if (iy + row.Height >= _resultsArea.Y && iy <= _resultsArea.Bottom)
                {
                    var bounds = new Rectangle(_resultsArea.X, iy, _resultsArea.Width, row.Height - (row is ItemRow ? 4 : 0));
                    if (row is HeaderRow hr) DrawGroupHeader(b, hr, bounds);
                    else if (row is ItemRow ir) DrawResultItem(b, ir.Subject, bounds, idx);
                }
                acc += row.Height;
                idx++;
            }
        }

        private void DrawGroupHeader(SpriteBatch b, HeaderRow hr, Rectangle bounds)
        {
            var clip = Rectangle.Intersect(bounds, _resultsArea);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y + bounds.Height / 2, bounds.Width, 1), new Color(199, 168, 120) * 0.6f);
            var sz = Game1.tinyFont.MeasureString(hr.Text);
            var pos = new Vector2(bounds.X + (bounds.Width - sz.X) / 2f, bounds.Y + (bounds.Height - sz.Y) / 2f);
            b.Draw(Game1.staminaRect, new Rectangle((int)pos.X - 6, bounds.Y, (int)sz.X + 12, bounds.Height), new Color(244, 226, 184));
            Utility.drawTextWithShadow(b, hr.Text, Game1.tinyFont, pos, new Color(138, 106, 69));
        }

        private static Color CategoryStripe(string cat) => cat switch
        {
            "NPCs" => new Color(74, 156, 74),
            "Monsters" => new Color(192, 64, 64),
            "Items" => new Color(217, 165, 32),
            "Buildings" => new Color(58, 111, 181),
            _ => new Color(150, 150, 150),
        };

        private void DrawResultItem(SpriteBatch b, SubjectWrapper s, Rectangle bounds, int idx)
        {
            var clip = Rectangle.Intersect(bounds, _resultsArea);
            if (clip.Width <= 0 || clip.Height <= 0) return;

            bool isClone = !s.HasRealData();
            bool isLockedNpc = s.GetCategory() == "NPCs" && !s.NpcHasBeenMet();

            b.Draw(Game1.staminaRect, clip, (idx % 2 == 0 ? new Color(180, 140, 90) * 0.10f : new Color(180, 140, 90) * 0.05f));
            b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 5, bounds.Height), CategoryStripe(s.GetCategory()) * (isClone ? 0.35f : 1f));

            int iconSize = bounds.Height - 12;
            var iconPos = new Vector2(bounds.X + 12, bounds.Y + 6);

            if (iconPos.Y >= _resultsArea.Y - iconSize && iconPos.Y < _resultsArea.Bottom)
            {
                b.Draw(Game1.staminaRect, new Rectangle((int)iconPos.X - 2, (int)iconPos.Y - 2, iconSize + 4, iconSize + 4), Color.Black * 0.15f);

                // Hard clip via GPU scissor rectangle - guarantees no pixel
                // can render outside this exact box, regardless of what
                // the monster's sprite dimensions/frame actually are. The
                // math-only approach (deriving scale from the source
                // rect) turned out not to fully prevent the overflow in
                // practice, so this is a stronger, unconditional fallback.
                var iconClipRect = new Rectangle((int)iconPos.X, (int)iconPos.Y, iconSize, iconSize);
                var safeClip = Rectangle.Intersect(iconClipRect, b.GraphicsDevice.Viewport.Bounds);
                if (safeClip.Width > 0 && safeClip.Height > 0)
                {
                    var prevScissor = b.GraphicsDevice.ScissorRectangle;
                    b.End();
                    b.GraphicsDevice.ScissorRectangle = safeClip;
                    b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null,
                            new RasterizerState { ScissorTestEnable = true });
                    if (!s.DrawPortrait(b, iconPos, new Vector2(iconSize)))
                    {
                        b.Draw(Game1.staminaRect, new Rectangle((int)iconPos.X, (int)iconPos.Y, iconSize, iconSize), Color.Gray * 0.3f);
                    }
                    b.End();
                    b.GraphicsDevice.ScissorRectangle = prevScissor;
                    b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                }
            }

            int textX = bounds.X + iconSize + 26;
            int textY = bounds.Y + 8;

            Color nameColor = isClone ? new Color(180, 170, 155) : (isLockedNpc ? new Color(154, 131, 104) : new Color(58, 37, 16));

            int starRight = STAR_SIZE + 16;
            int maxW = bounds.Right - textX - starRight - 10;
            string name = Truncate(s.Name, Game1.smallFont, maxW - 90);
            if (textY >= _resultsArea.Y && textY < _resultsArea.Bottom)
                DrawHighlighted(b, name, textX, textY, nameColor);

            // Small status tags after the name
            float nameW = Game1.smallFont.MeasureString(name).X;
            float tagX = textX + nameW + 8;
            if (isClone)
                DrawTag(b, "no data", tagX, textY, new Color(210, 210, 210), new Color(120, 120, 120));
            else if (isLockedNpc)
                DrawTag(b, "not unlocked", tagX, textY, new Color(224, 208, 176), new Color(122, 90, 48));

            float nameH = Game1.smallFont.MeasureString(name).Y;
            float descY = textY + nameH + 4f;
            if (!isClone && !string.IsNullOrEmpty(s.Description) && descY < bounds.Bottom - 20)
            {
                const float descScale = 0.6f;
                int descMaxW = (int)((bounds.Right - textX - starRight - 10) / descScale);
                string desc = Truncate(s.Description, Game1.smallFont, descMaxW);
                if (descY >= _resultsArea.Y && descY < _resultsArea.Bottom - 10)
                {
                    var dp = new Vector2(textX, descY);
                    b.DrawString(Game1.smallFont, desc, dp + new Vector2(1, 1), Color.Black * 0.25f, 0, Vector2.Zero, descScale, SpriteEffects.None, 0);
                    b.DrawString(Game1.smallFont, desc, dp, new Color(120, 105, 90), 0, Vector2.Zero, descScale, SpriteEffects.None, 0);
                }
            }

            if (_persistence != null && !isClone)
            {
                var starRect = StarBounds(bounds);
                if (starRect.Y >= _resultsArea.Y - STAR_SIZE && starRect.Y < _resultsArea.Bottom)
                {
                    bool fav = _persistence.IsFavorite(s.InternalName);
                    DrawStarShape(b, starRect, fav);
                }
            }
        }

        // Drawn as a small diamond shape rather than a "*"/"o" character -
        // the lowercase "o" for the unfavorited state turned out to render
        // small enough to look just like the digit "0" in the game's
        // font, which read as a confusing stray number next to every row.
        private static void DrawStarShape(SpriteBatch b, Rectangle bounds, bool filled)
        {
            // Previous unfavorited color (light tan/grey) blended into the
            // parchment background almost invisibly. Use a dark brown
            // outline instead - always visible against the light
            // background regardless of favorite state. The filled (already
            // bookmarked) gold fill ALSO blended into the parchment at
            // small size with no border, so it now always draws a dark
            // outline ring first, then the gold fill inside it.
            var fillColor = new Color(232, 180, 40);
            var outlineColor = new Color(90, 60, 20);
            int cx = bounds.X + bounds.Width / 2;
            int cy = bounds.Y + bounds.Height / 2;
            int r = Math.Min(bounds.Width, bounds.Height) / 2 - 2;
            for (int dy = -r; dy <= r; dy++)
            {
                int rowHalfW = r - Math.Abs(dy);
                if (rowHalfW <= 0) continue;
                if (filled)
                {
                    b.Draw(Game1.staminaRect, new Rectangle(cx - rowHalfW, cy + dy, rowHalfW * 2, 1), fillColor);
                    // 1px outline ring around the filled diamond so it
                    // still reads clearly against a similarly-toned
                    // background instead of just blending into it.
                    b.Draw(Game1.staminaRect, new Rectangle(cx - rowHalfW, cy + dy, 2, 1), outlineColor);
                    b.Draw(Game1.staminaRect, new Rectangle(cx + rowHalfW - 2, cy + dy, 2, 1), outlineColor);
                }
                else
                {
                    // outline only, but 3px thick (not 2px) so it stays
                    // clearly visible at this small size.
                    b.Draw(Game1.staminaRect, new Rectangle(cx - rowHalfW, cy + dy, 3, 1), outlineColor);
                    b.Draw(Game1.staminaRect, new Rectangle(cx + rowHalfW - 3, cy + dy, 3, 1), outlineColor);
                }
            }
        }

        private static void DrawTag(SpriteBatch b, string text, float x, float y, Color bg, Color fg)
        {
            var sz = Game1.tinyFont.MeasureString(text);
            var rect = new Rectangle((int)x, (int)y, (int)sz.X + 10, (int)sz.Y + 4);
            b.Draw(Game1.staminaRect, rect, bg);
            b.DrawString(Game1.tinyFont, text, new Vector2(x + 5, y + 2), fg);
        }

        private void DrawHighlighted(SpriteBatch b, string name, int x, int y, Color baseColor)
        {
            string q = (_searchBox.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(q))
            {
                int hi = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (hi >= 0)
                {
                    string before = name[..hi];
                    string match = name.Substring(hi, Math.Min(q.Length, name.Length - hi));
                    string after = name[(hi + match.Length)..];
                    float cx = x;
                    if (before.Length > 0) { Utility.drawTextWithShadow(b, before, Game1.smallFont, new Vector2(cx, y), baseColor); cx += Game1.smallFont.MeasureString(before).X; }
                    if (match.Length > 0)
                    {
                        var mSz = Game1.smallFont.MeasureString(match);
                        b.Draw(Game1.staminaRect, new Rectangle((int)cx - 1, y - 1, (int)mSz.X + 2, (int)mSz.Y + 2), new Color(224, 169, 48) * 0.35f);
                        Utility.drawTextWithShadow(b, match, Game1.smallFont, new Vector2(cx, y), new Color(150, 100, 10));
                        cx += mSz.X;
                    }
                    if (after.Length > 0) Utility.drawTextWithShadow(b, after, Game1.smallFont, new Vector2(cx, y), baseColor);
                    return;
                }
            }
            Utility.drawTextWithShadow(b, name, Game1.smallFont, new Vector2(x, y), baseColor);
        }

        // Drawn as plain colored boxes rather than arrow glyphs/text - the
        // game's bitmap fonts don't reliably render arrow/triangle Unicode
        // characters (same issue hit earlier with emoji), so this uses
        // guaranteed-safe rectangle shading instead of any character.
        private void DrawScrollArrows(SpriteBatch b)
        {
            bool canUp = _scroll > 0;
            bool canDown = _scroll < _maxScroll;
            DrawArrowButton(b, _scrollUpButtonBounds, up: true, canUp);
            DrawArrowButton(b, _scrollDownButtonBounds, up: false, canDown);
        }

        private static void DrawArrowButton(SpriteBatch b, Rectangle bounds, bool up, bool enabled)
        {
            var bg = enabled ? new Color(216, 189, 142) : new Color(180, 170, 155) * 0.5f;
            b.Draw(Game1.staminaRect, bounds, bg);
            var fg = enabled ? new Color(74, 47, 20) : Color.Gray;
            int w = bounds.Width / 2;
            int cx = bounds.X + bounds.Width / 2;
            int topY = up ? bounds.Y + bounds.Height / 4 : bounds.Bottom - bounds.Height / 4 - 2;
            int rows = bounds.Height / 3;
            for (int i = 0; i < rows; i++)
            {
                int rowW = w - i * (w / Math.Max(rows, 1));
                int y = up ? topY + i * 2 : topY - i * 2;
                b.Draw(Game1.staminaRect, new Rectangle(cx - rowW / 2, y, Math.Max(rowW, 2), 2), fg);
            }
        }

        private void DrawScrollbar(SpriteBatch b)
        {
            if (_maxScroll <= 0) return;
            b.Draw(Game1.staminaRect, _scrollbarArea, Color.Black * 0.25f);
            int total = Math.Max(1, _rows.Sum(r => r.Height));
            float ratio = (float)_resultsArea.Height / total;
            int handleH = Math.Max(30, (int)(_scrollbarArea.Height * ratio));
            float pos = _maxScroll > 0 ? _scroll / _maxScroll : 0f;
            int handleY = _scrollbarArea.Y + (int)((_scrollbarArea.Height - handleH) * pos);
            b.Draw(Game1.staminaRect, new Rectangle(_scrollbarArea.X, handleY, _scrollbarArea.Width, handleH), new Color(122, 74, 43) * 0.8f);
        }

        private void DrawCount(SpriteBatch b)
        {
            string text;
            if (!_fullyLoaded)
            {
                int pct = _rawSubjects.Count > 0 ? _wrapIndex * 100 / _rawSubjects.Count : 0;
                text = $"Loading... {pct}%";
            }
            else
            {
                int shown = _rows.Count(r => r is ItemRow);
                text = $"{shown} shown";
                string q = (_searchBox.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(q))
                    text = $"'{Truncate(q, Game1.smallFont, 100)}': {text}";
            }
            var pos = new Vector2(_resultsArea.X, _resultsArea.Bottom + 4);
            b.DrawString(Game1.smallFont, text, pos + new Vector2(1, 1), Color.Black * 0.3f, 0, Vector2.Zero, 0.8f, SpriteEffects.None, 0.99f);
            b.DrawString(Game1.smallFont, text, pos, Color.Gray, 0, Vector2.Zero, 0.8f, SpriteEffects.None, 1f);
        }

        // ──────────────── Helpers ────────────────

        private string Truncate(string text, SpriteFont font, int maxW)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\n", " ").Replace("\r", " ");
            if (maxW <= 0) return "";
            if (font.MeasureString(text).X <= maxW) return text;
            const string ellipsis = "...";
            float ellW = font.MeasureString(ellipsis).X;
            for (int i = text.Length - 1; i > 0; i--)
            {
                string sub = text[..i];
                if (font.MeasureString(sub).X <= maxW - ellW)
                    return sub.TrimEnd() + ellipsis;
            }
            return ellipsis;
        }
    }
}
