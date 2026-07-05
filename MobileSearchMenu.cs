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
        private const int CATEGORY_HEIGHT = 44;
        private const int ITEM_HEIGHT = 80;
        private const int SCROLLBAR_WIDTH = 12;
        private const float TAP_THRESHOLD = 20f;
        private const int BATCH_SIZE = 50;

        private class CategoryButton
        {
            public Rectangle Bounds { get; set; }
            public string Category { get; set; } = "";
            public bool IsSelected { get; set; }
        }

        private readonly bool _isAndroid = (int)Constants.TargetPlatform == 0;
        private List<object> _rawSubjects;
        private readonly List<SubjectWrapper> _wrapped = new();
        private List<SubjectWrapper> _filtered = new();
        private readonly Action<object> _onSelect;
        private readonly Func<List<object>>? _monsterProvider;
        private bool _monstersLoaded;

        private int _wrapIndex;
        private bool _fullyLoaded;
        private bool _needsFilter = true;

        private readonly List<CategoryButton> _categories = new();
        private string _currentCategory = "All";

        private TextBox _searchBox = null!;
        private Rectangle _searchBoxBounds;
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
        private Rectangle _resultsArea;
        private Rectangle _scrollbarArea;
        private int _maxVisible;

        public MobileSearchMenu(IEnumerable<object> subjects, Action<object> onSelect, Func<List<object>>? monsterProvider = null)
            : base(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height, false)
        {
            _onSelect = onSelect;
            _rawSubjects = subjects.ToList();
            _monsterProvider = monsterProvider;

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

        private void CalculateLayout()
        {
            int margin = Math.Min(24, Game1.uiViewport.Width / 25);
            xPositionOnScreen = margin;
            yPositionOnScreen = margin;
            width = Game1.uiViewport.Width - margin * 2;
            height = Game1.uiViewport.Height - margin * 2;

            int cx = xPositionOnScreen + PADDING;
            int cw = width - PADDING * 2 - SCROLLBAR_WIDTH - 8;
            int cy = yPositionOnScreen + PADDING + 40;

            _searchBoxBounds = new Rectangle(cx + 20, cy, cw - 56, SEARCH_BOX_HEIGHT);
            cy += SEARCH_BOX_HEIGHT + 16;
            _categoryArea = new Rectangle(cx, cy, cw, CATEGORY_HEIGHT);
            cy += CATEGORY_HEIGHT + 16;
            _resultsArea = new Rectangle(cx + 14, cy, cw - 20, yPositionOnScreen + height - cy - PADDING - 24);
            _scrollbarArea = new Rectangle(_resultsArea.Right + 8, _resultsArea.Y, SCROLLBAR_WIDTH, _resultsArea.Height);
            _maxVisible = _resultsArea.Height / ITEM_HEIGHT;
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
                new Rectangle(_searchBoxBounds.Right + 8, _searchBoxBounds.Y + (_searchBoxBounds.Height - 44) / 2, 44, 44),
                Game1.mouseCursors, new Rectangle(322, 498, 12, 12), 3.5f);

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 56 - 8, yPositionOnScreen + 8, 56, 56),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 4.667f);

            RebuildCategoryButtons(new List<string> { "All", "Items", "NPCs", "Monsters", "Buildings" });
        }

        private void RebuildCategoryButtons(List<string> cats)
        {
            _categories.Clear();
            int btnW = (_categoryArea.Width - (cats.Count - 1) * 6) / cats.Count;
            int x = _categoryArea.X;
            foreach (var cat in cats)
            {
                _categories.Add(new CategoryButton
                {
                    Bounds = new Rectangle(x, _categoryArea.Y, btnW, CATEGORY_HEIGHT),
                    Category = cat,
                    IsSelected = cat == _currentCategory
                });
                x += btnW + 6;
            }
        }

        // ──────────────── Input ────────────────

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y)) { exitThisMenu(); return; }

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

            if (!string.IsNullOrEmpty(_searchBox.Text) && _clearButton.bounds.Contains(x, y))
            {
                _searchBox.Text = "";
                _needsFilter = true;
                Game1.playSound("smallSelect");
                return;
            }

            foreach (var btn in _categories)
            {
                if (!btn.Bounds.Contains(x, y)) continue;
                if (_currentCategory == btn.Category) return;
                _currentCategory = btn.Category;
                foreach (var b2 in _categories) b2.IsSelected = b2.Category == _currentCategory;
                if (btn.Category == "Monsters") {
                    EnsureMonstersLoaded();
                }
                _needsFilter = true;
                if (playSound) Game1.playSound("smallSelect");
                return;
            }

            if (_resultsArea.Contains(x, y))
            {
                _dragging = true;
                _dragStart = _lastDrag = new Vector2(x, y);
                _dragDist = 0;
            }
        }

        public override void leftClickHeld(int x, int y)
        {
            if (!_dragging) return;
            var cur = new Vector2(x, y);
            float delta = _lastDrag.Y - y;
            _dragDist += Vector2.Distance(_lastDrag, cur);
            _scroll = MathHelper.Clamp(_scroll + delta, 0, _maxScroll);
            _lastDrag = cur;
        }

        public override void releaseLeftClick(int x, int y)
        {
            if (_dragging && _dragDist < TAP_THRESHOLD)
                TrySelect((int)_dragStart.X, (int)_dragStart.Y);
            _dragging = false;
            _dragDist = 0;
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
                else exitThisMenu();
            }
        }

        // ──────────────── Lazy monster loading ────────────────

        // Building monster preview subjects is the expensive part (each one
        // constructs a throwaway Monster + its Lookup Anything subject), so
        // it only happens the first time the player actually opens the
        // Monsters tab, not every time this menu opens or the game loads.
        private void EnsureMonstersLoaded()
        {
            if (_monstersLoaded || _monsterProvider == null) {
                return;
            }
            _monstersLoaded = true;
            List<object> monsters = _monsterProvider.Invoke() ?? new List<object>();
            if (monsters.Count == 0) {
                return;
            }
            _rawSubjects = _rawSubjects.Concat(monsters).ToList();
            // Resume the existing incremental wrapper so the new entries get
            // spread across frames like everything else, instead of
            // wrapping all of them in one go.
            _fullyLoaded = false;
        }

        private void TrySelect(int x, int y)
        {
            if (!_resultsArea.Contains(x, y)) return;
            int idx = (y - _resultsArea.Y + (int)_scroll) / ITEM_HEIGHT;
            if (idx >= 0 && idx < _filtered.Count)
            {
                var subject = _filtered[idx];
                exitThisMenu(false);
                Game1.playSound("select");
                _onSelect?.Invoke(subject.RawSubject);
            }
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
                _wrapped.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                UpdateCategoryButtons();
            }
            _needsFilter = true;
        }

        private void UpdateCategoryButtons()
        {
            var cats = _wrapped.Select(s => s.GetCategory()).Distinct().OrderBy(c => c).ToList();
            var list = new List<string> { "All" };
            list.AddRange(cats);
            if (list.Count > 6) list = list.Take(6).ToList();
            RebuildCategoryButtons(list);
        }

        private void ApplyFilter()
        {
            string q = (_searchBox.Text ?? "").Trim().ToLowerInvariant();
            _filtered = _wrapped.Where(s =>
            {
                if (_currentCategory != "All" && s.GetCategory() != _currentCategory) return false;
                return string.IsNullOrEmpty(q) || s.Name.ToLowerInvariant().Contains(q) || s.Description.ToLowerInvariant().Contains(q);
            }).ToList();

            if (!string.IsNullOrEmpty(q))
                _filtered = _filtered.OrderBy(s => !s.Name.ToLowerInvariant().StartsWith(q)).ThenBy(s => s.Name).ToList();

            _scroll = 0;
            int total = _filtered.Count * ITEM_HEIGHT;
            _maxScroll = Math.Max(0, total - _resultsArea.Height);
            _needsFilter = false;
        }

        // ──────────────── Draw ────────────────

        public override void draw(SpriteBatch b)
        {
            // Backdrop
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.75f);

            // Menu box
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            // Title
            string title = "Search Encyclopedia";
            float scale = 0.75f;
            var titleSize = Game1.dialogueFont.MeasureString(title) * scale;
            var titlePos = new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 16);
            b.DrawString(Game1.dialogueFont, title, titlePos + new Vector2(2, 2), Color.Black * 0.5f, 0, Vector2.Zero, scale, SpriteEffects.None, 1f);
            b.DrawString(Game1.dialogueFont, title, titlePos, Game1.textColor, 0, Vector2.Zero, scale, SpriteEffects.None, 1f);

            _closeButton.draw(b);
            DrawSearchBox(b);
            if (!string.IsNullOrEmpty(_searchBox.Text)) _clearButton.draw(b);
            DrawCategories(b);

            b.Draw(Game1.staminaRect, _resultsArea, Color.Black * 0.25f);
            DrawResults(b);
            DrawScrollbar(b);
            DrawCount(b);

            drawMouse(b);
        }

        private void DrawSearchBox(SpriteBatch b)
        {
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                _searchBoxBounds.X - 4, _searchBoxBounds.Y - 4,
                _searchBoxBounds.Width + 8, _searchBoxBounds.Height + 8, Color.White, drawShadow: false);
            b.Draw(Game1.staminaRect, _searchBoxBounds, Color.White);
            _searchIcon.draw(b, Color.Gray, 1f);
            _searchBox.Draw(b, true);
            if (string.IsNullOrEmpty(_searchBox.Text) && !_searchBox.Selected)
                Utility.drawTextWithShadow(b, "Tap to search...", Game1.smallFont,
                    new Vector2(_searchBox.X + 15, _searchBox.Y + 4), Color.Gray);
        }

        private void DrawCategories(SpriteBatch b)
        {
            foreach (var btn in _categories)
            {
                var bgColor = btn.IsSelected ? new Color(100, 150, 100) : new Color(80, 80, 80);
                var textColor = btn.IsSelected ? Color.White : Color.LightGray;
                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                    btn.Bounds.X, btn.Bounds.Y, btn.Bounds.Width, btn.Bounds.Height, bgColor, drawShadow: false);
                string label = Truncate(btn.Category, Game1.smallFont, btn.Bounds.Width - 12);
                var labelSize = Game1.smallFont.MeasureString(label);
                var pos = new Vector2(
                    btn.Bounds.X + (btn.Bounds.Width - labelSize.X) / 2f,
                    btn.Bounds.Y + (btn.Bounds.Height - labelSize.Y) / 2f);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, pos, textColor);
            }
        }

        private void DrawResults(SpriteBatch b)
        {
            if (_filtered.Count == 0)
            {
                string msg = _fullyLoaded ? "No results found" : "Loading...";
                var sz = Game1.smallFont.MeasureString(msg);
                Utility.drawTextWithShadow(b, msg, Game1.smallFont,
                    new Vector2(_resultsArea.X + (_resultsArea.Width - sz.X) / 2f,
                                _resultsArea.Y + (_resultsArea.Height - sz.Y) / 2f), Color.Gray);
                return;
            }

            int start = (int)(_scroll / ITEM_HEIGHT);
            int end = Math.Min(start + _maxVisible + 1, _filtered.Count);
            for (int i = start; i < end; i++)
            {
                int iy = _resultsArea.Y + i * ITEM_HEIGHT - (int)_scroll;
                if (iy + ITEM_HEIGHT < _resultsArea.Y || iy > _resultsArea.Bottom) continue;
                var bounds = new Rectangle(_resultsArea.X, iy, _resultsArea.Width, ITEM_HEIGHT - 4);
                DrawResultItem(b, _filtered[i], bounds, i);
                int sepY = iy + ITEM_HEIGHT - 4;
                if (sepY > _resultsArea.Y && sepY < _resultsArea.Bottom - 4)
                    b.Draw(Game1.staminaRect, new Rectangle(_resultsArea.X + 8, sepY, _resultsArea.Width - 16, 1), Color.White * 0.2f);
            }
        }

        private void DrawResultItem(SpriteBatch b, SubjectWrapper s, Rectangle bounds, int idx)
        {
            var clip = Rectangle.Intersect(bounds, _resultsArea);
            if (clip.Width <= 0 || clip.Height <= 0) return;

            b.Draw(Game1.staminaRect, clip, (idx % 2 == 0 ? Color.White * 0.08f : Color.White * 0.04f));

            int iconSize = bounds.Height - 12;
            var iconPos = new Vector2(bounds.X + 6, bounds.Y + 6);

            if (iconPos.Y >= _resultsArea.Y - iconSize && iconPos.Y < _resultsArea.Bottom)
            {
                b.Draw(Game1.staminaRect, new Rectangle((int)iconPos.X - 2, (int)iconPos.Y - 2, iconSize + 4, iconSize + 4), Color.Black * 0.2f);
                if (!s.DrawPortrait(b, iconPos, new Vector2(iconSize)))
                {
                    b.Draw(Game1.staminaRect, new Rectangle((int)iconPos.X, (int)iconPos.Y, iconSize, iconSize), Color.Gray * 0.3f);
                    string abbr = CategoryAbbr(s.GetCategory());
                    var abbrSz = Game1.smallFont.MeasureString(abbr);
                    Utility.drawTextWithShadow(b, abbr, Game1.tinyFont,
                        new Vector2(iconPos.X + (iconSize - abbrSz.X) / 2f, iconPos.Y + (iconSize - abbrSz.Y) / 2f), Color.White);
                }
            }

            int textX = bounds.X + iconSize + 20;
            int textY = bounds.Y + 8;

            // Category badge
            string cat = s.GetCategory();
            var catSz = Game1.tinyFont.MeasureString(cat);
            int badgeW = (int)(catSz.X + 12);
            int badgeH = (int)(catSz.Y + 6);
            int badgeX = bounds.Right - badgeW - 8;
            if (textY >= _resultsArea.Y - 10 && textY < _resultsArea.Bottom)
            {
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 373, 9, 9),
                    badgeX, textY, badgeW, badgeH, Color.DarkSlateBlue * 0.9f, 2f, false);
                b.DrawString(Game1.tinyFont, cat, new Vector2(badgeX + 6, textY + (badgeH - catSz.Y) / 2f), Color.LightBlue);
            }

            // Name
            int maxW = badgeX - textX - 12;
            string name = Truncate(s.Name, Game1.smallFont, maxW - 80);
            DrawHighlighted(b, name, textX, textY, maxW);

            // Description
            float nameH = Game1.smallFont.MeasureString(name).Y;
            float descY = textY + nameH + 4f;
            if (!string.IsNullOrEmpty(s.Description) && descY < bounds.Bottom - 20)
            {
                const float descScale = 0.6f;
                int descMaxW = (int)((bounds.Right - textX - 10) / descScale);
                string desc = Truncate(s.Description, Game1.smallFont, descMaxW);
                if (descY >= _resultsArea.Y && descY < _resultsArea.Bottom - 10)
                {
                    var dp = new Vector2(textX, descY);
                    b.DrawString(Game1.smallFont, desc, dp + new Vector2(1, 1), Color.Black * 0.35f, 0, Vector2.Zero, descScale, SpriteEffects.None, 0);
                    b.DrawString(Game1.smallFont, desc, dp, Color.Gray, 0, Vector2.Zero, descScale, SpriteEffects.None, 0);
                }
            }
        }

        private void DrawHighlighted(SpriteBatch b, string name, int x, int y, int maxW)
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
                    if (before.Length > 0) { Utility.drawTextWithShadow(b, before, Game1.smallFont, new Vector2(cx, y), Color.White); cx += Game1.smallFont.MeasureString(before).X; }
                    if (match.Length > 0)
                    {
                        var mSz = Game1.smallFont.MeasureString(match);
                        b.Draw(Game1.staminaRect, new Rectangle((int)cx - 1, y - 1, (int)mSz.X + 2, (int)mSz.Y + 2), Color.Yellow * 0.3f);
                        Utility.drawTextWithShadow(b, match, Game1.smallFont, new Vector2(cx, y), Color.Yellow);
                        cx += mSz.X;
                    }
                    if (after.Length > 0) Utility.drawTextWithShadow(b, after, Game1.smallFont, new Vector2(cx, y), Color.White);
                    return;
                }
            }
            Utility.drawTextWithShadow(b, name, Game1.smallFont, new Vector2(x, y), Color.White);
        }

        private void DrawScrollbar(SpriteBatch b)
        {
            if (_maxScroll <= 0) return;
            b.Draw(Game1.staminaRect, _scrollbarArea, Color.Black * 0.3f);
            float ratio = (float)_resultsArea.Height / (_filtered.Count * ITEM_HEIGHT);
            int handleH = Math.Max(30, (int)(_scrollbarArea.Height * ratio));
            float pos = _maxScroll > 0 ? _scroll / _maxScroll : 0f;
            int handleY = _scrollbarArea.Y + (int)((_scrollbarArea.Height - handleH) * pos);
            b.Draw(Game1.staminaRect, new Rectangle(_scrollbarArea.X, handleY, _scrollbarArea.Width, handleH), Color.White * 0.7f);
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
                text = _filtered.Count == _wrapped.Count
                    ? $"{_wrapped.Count} items"
                    : $"{_filtered.Count} of {_wrapped.Count}";
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

        private static string CategoryAbbr(string cat) => cat switch
        {
            "NPCs" => "NPC", "Items" => "ITM", "Buildings" => "BLD",
            "Animals" => "ANM", "Crops" => "CRP", "Terrain" => "TRN",
            "Monsters" => "MON", _ => "???"
        };
    }
}
