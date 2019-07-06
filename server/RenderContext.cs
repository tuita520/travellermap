//#define SHOW_TIMING

using Maps.Graphics;
using Maps.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.RegularExpressions;

namespace Maps.Rendering
{
    internal class RenderContext
    {
        public RenderContext(ResourceManager resourceManager, Selector selector, RectangleF tileRect,
            double scale, MapOptions options, Stylesheet styles, Size tileSize)
        {
            this.resourceManager = resourceManager;
            this.selector = selector;
            this.tileRect = tileRect;
            this.scale = scale;
            this.options = options;
            this.styles = styles;
            this.tileSize = tileSize;

            selector.UseMilieuFallbacks = true;

            AbstractMatrix m = AbstractMatrix.Identity;
            m.TranslatePrepend((float)(-tileRect.Left * scale * Astrometrics.ParsecScaleX), (float)(-tileRect.Top * scale * Astrometrics.ParsecScaleY));
            m.ScalePrepend((float)scale * Astrometrics.ParsecScaleX, (float)scale * Astrometrics.ParsecScaleY);
            ImageSpaceToWorldSpace = m;
            m.Invert();
            worldSpaceToImageSpace = m;
        }

        // Required
        private readonly ResourceManager resourceManager;
        private readonly Selector selector;
        private readonly RectangleF tileRect;
        private readonly double scale;
        private readonly MapOptions options;
        private readonly Stylesheet styles;
        private readonly Size tileSize;

        // Options
        public AbstractPath ClipPath { get; set; }
        public bool DrawBorder { get; set; }
        public bool ClipOutsectorBorders { get; set; }

        // Assigned during Render()
        private AbstractGraphics graphics;
        private AbstractBrush solidBrush;
        private AbstractPen pen;
        private FontCache fonts;

        public Stylesheet Styles => styles;
        private readonly AbstractMatrix worldSpaceToImageSpace;
        public AbstractMatrix ImageSpaceToWorldSpace { get; }

        private static readonly RectangleF galaxyImageRect = new Rectangle(-18257, -26234, 36551, 32462); // Chosen to match T5 pp.416

        private static readonly Rectangle riftImageRect = new Rectangle(-1374, -827, 2769, 1754);

        #region labels
        private class MapLabel
        {
            public MapLabel(string text, float x, float y, bool minor = false) { this.text = text; position = new PointF(x, y); this.minor = minor; }
            public readonly string text;
            public readonly PointF position;
            public readonly bool minor;
        }

        // TODO: Move this to data file
        private static readonly MapLabel[] minorLabels =
        {
            new MapLabel("Human Client States", -184, -50),
            new MapLabel("Aslan Client States", -69, 155),
            new MapLabel("Aslan Colonies", -133, -5),
            new MapLabel("Mixed Client States", 127, 5),
            new MapLabel("Scattered\nClient States", 98, 65),
            new MapLabel("Vargr Enclaves", 110, -135),
            new MapLabel("Hive Young Worlds", 115, 128)
        };

        // TODO: Move this to data file
        private static readonly MapLabel[] megaLabels =
        {
            new MapLabel("Charted Space", 0, 400, minor:true),
            new MapLabel("Zhodani\nCore\nExpeditions", 0, -3500, minor:true),
            new MapLabel("Core Sophonts", 0, -12500),
            new MapLabel("Abyssals", -15000, -7500),
            new MapLabel("Denizens", -8660, -10000),
            new MapLabel("Essaray", 11000, -16000),
            new MapLabel("Anomaly One", 0, -22000, minor:true),
            new MapLabel("Dushis Khurisi", 15000, -8500, minor:true),
            new MapLabel("The\nBarren\nArm", 9240, -4500, minor:true),
        };

        private static readonly string[] borderFiles = {
            @"~/res/Vectors/Imperium.xml",
            @"~/res/Vectors/Aslan.xml",
            @"~/res/Vectors/Kkree.xml",
            @"~/res/Vectors/Vargr.xml",
            @"~/res/Vectors/Zhodani.xml",
            @"~/res/Vectors/Solomani.xml",
            @"~/res/Vectors/Hive.xml",
            @"~/res/Vectors/SpinwardClient.xml",
            @"~/res/Vectors/RimwardClient.xml",
            @"~/res/Vectors/TrailingClient.xml"
        };

        private static readonly string[] riftFiles = {
            @"~/res/Vectors/GreatRift.xml",
            @"~/res/Vectors/LesserRift.xml",
            @"~/res/Vectors/WindhornRift.xml",
            @"~/res/Vectors/DelphiRift.xml",
            @"~/res/Vectors/ZhdantRift.xml"
        };

        private static readonly string[] routeFiles = {
            @"~/res/Vectors/J5Route.xml",
            @"~/res/Vectors/J4Route.xml",
            @"~/res/Vectors/CoreRoute.xml"
        };
        #endregion

        #region Static Caches
        private static object s_imageInitLock = new object();
        private static bool s_imagesInitialized = false;

        // TODO: Consider not caching these across sessions
        private static AbstractImage s_nebulaImage;
        private static AbstractImage s_galaxyImage;
        private static AbstractImage s_galaxyImageGray;
        private static AbstractImage s_riftImage;
        private static ConcurrentDictionary<string, AbstractImage> s_worldImages;
        #endregion

        #region Timers
        /// <summary>
        /// Performance timer record
        /// </summary>
        private class Timer
        {
#if SHOW_TIMING
            public DateTime dt = DateTime.Now;
            public string label;
            public Timer(string label)
            {
                this.label = label;
            }
#else
            public Timer(string label) { }
#endif
        }
        #endregion

        // Individual rendering step; used in the Render() call to order
        // each of the layers.
        private class LayerAction
        {
            public LayerAction(LayerId id, Action<RenderContext> action, bool clip)
            {
                this.id = id;
                this.action = action;
                this.clip = clip;
            }

            public void Run(RenderContext context)
            {
                action.Invoke(context);
            }

            public readonly LayerId id;
            private readonly Action<RenderContext> action;
            public readonly bool clip;
        }

        public void Render(AbstractGraphics g)
        {
#if SHOW_TIMING
            DateTime dtStart = DateTime.Now;
#endif

            graphics = g;
            solidBrush = new AbstractBrush();
            pen = new AbstractPen(Color.Empty);
            fonts = new FontCache(styles);

            InitializeImages();

            List<Timer> timers = new List<Timer>
            {
                new Timer("preload")
            };

            // Overall, rendering is all in world-space; individual steps may transform back
            // to image-space as needed.
            graphics.MultiplyTransform(ImageSpaceToWorldSpace);

            // Order here doesn't matter, as these will be sorted according to |styles.layerOrder|
            var layers = new List<LayerAction>
            {
                //------------------------------------------------------------
                // Background
                //------------------------------------------------------------

                new LayerAction(LayerId.Background_Solid, ctx => ctx.DrawBackground(), clip:true),

                // NOTE: Since alpha texture brushes aren't supported without
                // creating a new image (slow!) we render the local background
                // first, then overlay the deep background over it, for
                // basically the same effect since the alphas sum to 1.
                new LayerAction(LayerId.Background_NebulaTexture, ctx => ctx.DrawNebulaBackground(), clip:true),
                new LayerAction(LayerId.Background_Galaxy, ctx => ctx.DrawGalaxyBackground(), clip:true),

                new LayerAction(LayerId.Background_PseudoRandomStars, ctx => ctx.DrawPseudoRandomStars(), clip:true),
                new LayerAction(LayerId.Background_Rifts, ctx => ctx.DrawRifts(), clip:true),

                //------------------------------------------------------------
                // Foreground
                //------------------------------------------------------------
                
                new LayerAction(LayerId.Macro_Borders, ctx => ctx.DrawMacroBorders(), clip:true),
                new LayerAction(LayerId.Macro_Routes, ctx => ctx.DrawMacroRoutes(), clip:true),

                new LayerAction(LayerId.Grid_Sector, ctx => ctx.DrawSectorGrid(), clip:true),
                new LayerAction(LayerId.Grid_Subsector, ctx => ctx.DrawSubsectorGrid(), clip:true),
                new LayerAction(LayerId.Grid_Parsec, ctx => ctx.DrawParsecGrid(), clip:true),

                new LayerAction(LayerId.Names_Subsector, ctx => ctx.DrawSubsectorNames(), clip:true),

                new LayerAction(LayerId.Micro_BordersFill, ctx => ctx.DrawMicroBordersFill(), clip:true),
                new LayerAction(LayerId.Micro_BordersStroke, ctx => ctx.DrawMicroBordersStroke(), clip:true),
                new LayerAction(LayerId.Micro_Routes, ctx => ctx.DrawMicroRoutes(), clip:true),
                new LayerAction(LayerId.Micro_BorderExplicitLabels, ctx => ctx.DrawMicroLabels(), clip:true),

                new LayerAction(LayerId.Names_Sector, ctx => ctx.DrawSectorNames(), clip:true),
                new LayerAction(LayerId.Macro_GovernmentRiftRouteNames, ctx => ctx.DrawMacroNames(), clip:true),
                new LayerAction(LayerId.Macro_CapitalsAndHomeWorlds, ctx => ctx.DrawCapitalsAndHomeWorlds(), clip:true),
                new LayerAction(LayerId.Mega_GalaxyScaleLabels, ctx => ctx.DrawMegaLabels(), clip:true),

                new LayerAction(LayerId.Worlds_Background, ctx => ctx.DrawWorldsBackground(), clip:true),

                // Not clipped, so names are not clipped in jumpmaps.
                new LayerAction(LayerId.Worlds_Foreground, ctx => ctx.DrawWorldsForeground(), clip:false),

                new LayerAction(LayerId.Worlds_Overlays, ctx => ctx.DrawWorldsOverlay(), clip:true),

                //------------------------------------------------------------
                // Overlays
                //------------------------------------------------------------
                
                new LayerAction(LayerId.Overlay_DroyneChirperWorlds, ctx => ctx.DrawDroyneOverlay(), clip:true),
                new LayerAction(LayerId.Overlay_MinorHomeworlds, ctx => ctx.DrawMinorHomeworldOverlay(), clip:true),
                new LayerAction(LayerId.Overlay_AncientsWorlds, ctx => ctx.DrawAncientWorldsOverlay(), clip:true),
                new LayerAction(LayerId.Overlay_ReviewStatus, ctx => ctx.DrawSectorReviewStatusOverlay(), clip:true),
            };

            // Order per stylesheet
            layers.Sort((a, b) => styles.layerOrder[a.id] - styles.layerOrder[b.id]);

            //
            // Run the steps, imposing clipping region as needed.
            //

            AbstractGraphicsState state = null;
            foreach (var layer in layers)
            {
                // Impose a clipping region if desired, or remove it if not.
                if (layer.clip && state == null)
                {
                    state = graphics.Save();
                    if (ClipPath != null) graphics.IntersectClip(ClipPath);
                    else graphics.IntersectClip(tileRect);
                }
                else if (!layer.clip && state != null)
                {
                    state.Dispose();
                    state = null;
                }

                layer.Run(this);
                timers.Add(new Timer(layer.id.ToString()));
            }
            state?.Dispose();


            #region timing
#if SHOW_TIMING
                using( graphics.Save() )
                {
                    Font font = new Font( FontFamily.GenericSansSerif, 12, FontStyle.Regular);
                    graphics.MultiplyTransform( worldSpaceToImageSpace );
                    float cursorX = 20.0f, cursorY = 20.0f;
                    DateTime last = dtStart;
                    foreach ( Timer s in timers )
                    {
                        TimeSpan ts = s.dt - last;
                        last = s.dt;
                        double rounded = Math.Round(ts.TotalMilliseconds);
                        if (rounded == 0)
                            continue;

                        string str = $"{rounded} {s.label}";
                        for ( int dx = -1; dx <= 1; ++dx )
                        {
                            for( int dy = -1; dy <= 1; ++dy )
                            {
                                solidBrush.Color = Color.Black;
                                graphics.DrawString(str, font, solidBrush, cursorX + dx, cursorY + dy, StringAlignment.TopLeft);
                            }
                        }
                        solidBrush.Color = TravellerColors.Amber;
                        graphics.DrawString(str, font, solidBrush, cursorX, cursorY, StringAlignment.TopLeft);
                        cursorY += 14;
                    }
                }
#endif
            #endregion

            fonts.Dispose();
        }

        private void DrawSectorReviewStatusOverlay()
        {
            if (styles.dimUnofficialSectors && styles.worlds.visible)
            {
                solidBrush.Color = Color.FromArgb(128, styles.backgroundColor);
                foreach (Sector sector in selector.Sectors
                    .Where(sector => !sector.Tags.Contains("Official") && !sector.Tags.Contains("Preserve") && !sector.Tags.Contains("InReview")))
                    graphics.DrawRectangle(solidBrush, sector.Bounds);
            }
            if (styles.colorCodeSectorStatus && styles.worlds.visible)
            {
                foreach (Sector sector in selector.Sectors)
                {
                    if (sector.Tags.Contains("Official"))
                        solidBrush.Color = Color.FromArgb(128, TravellerColors.Red);
                    else if (sector.Tags.Contains("InReview"))
                        solidBrush.Color = Color.FromArgb(128, Color.Orange);
                    else if (sector.Tags.Contains("Unreviewed"))
                        solidBrush.Color = Color.FromArgb(128, TravellerColors.Amber);
                    else if (sector.Tags.Contains("Apocryphal"))
                        solidBrush.Color = Color.FromArgb(128, Color.Magenta);
                    else if (sector.Tags.Contains("Preserve"))
                        solidBrush.Color = Color.FromArgb(128, TravellerColors.Green);
                    else
                        continue;
                    graphics.DrawRectangle(solidBrush, sector.Bounds);
                }
            }
        }

        private void DrawAncientWorldsOverlay()
        {
            if (!styles.ancientsWorlds.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;
            solidBrush.Color = styles.ancientsWorlds.textColor;
            foreach (World world in selector.Worlds.Where(w => w.HasCode("An")))
            {
                OverlayGlyph(styles.ancientsWorlds.content, styles.ancientsWorlds.Font, world.Coordinates);
            }
        }

        private void DrawMinorHomeworldOverlay()
        {
            if (!styles.minorHomeWorlds.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;
            solidBrush.Color = styles.minorHomeWorlds.textColor;
            foreach (World world in selector.Worlds.Where(w => w.HasCodePrefix("(")))
            {
                OverlayGlyph(styles.minorHomeWorlds.content, styles.minorHomeWorlds.Font, world.Coordinates);
            }
        }

        private void DrawDroyneOverlay()
        {
            if (!styles.droyneWorlds.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;
            solidBrush.Color = styles.droyneWorlds.textColor;
            foreach (World world in selector.Worlds)
            {
                bool droyne = world.HasCodePrefix("Droy");
                bool chirpers = world.HasCodePrefix("Chir");

                if (droyne || chirpers)
                {
                    string glyph = droyne ? styles.droyneWorlds.content.Substring(0, 1) : styles.droyneWorlds.content.Substring(1, 1);
                    OverlayGlyph(glyph, styles.droyneWorlds.Font, world.Coordinates);
                }
            }
        }

        private void DrawWorldsBackground()
        {
            if (!styles.worlds.visible || styles.showStellarOverlay)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;
            foreach (World world in selector.Worlds)
                DrawWorld(world, WorldLayer.Background);
        }

        private void DrawWorldsForeground()
        {
            if (!styles.worlds.visible || styles.showStellarOverlay)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;
            foreach (World world in selector.Worlds)
                DrawWorld(world, WorldLayer.Foreground);
        }

        private void DrawWorldsOverlay()
        {
            if (!styles.worlds.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;
            if (styles.showStellarOverlay)
            {
                foreach (World world in selector.Worlds)
                    DrawStars(world);
            }
            else if (styles.HasWorldOverlays)
            {
                float slop = selector.SlopFactor;
                selector.SlopFactor = (float)Math.Max(slop, Math.Log(scale, 2.0) - 4);
                foreach (World world in selector.Worlds)
                    DrawWorld(world, WorldLayer.Overlay);
                selector.SlopFactor = slop;
            }
        }

        private void DrawMegaLabels()
        {
            if (!styles.megaNames.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;
            solidBrush.Color = styles.megaNames.textColor;
            foreach (var label in megaLabels)
            {
                using (graphics.Save())
                {
                    Font font = label.minor ? styles.megaNames.SmallFont : styles.megaNames.Font;
                    graphics.TranslateTransform(label.position.X, label.position.Y);
                    graphics.ScaleTransform(1.0f / Astrometrics.ParsecScaleX, 1.0f / Astrometrics.ParsecScaleY);
                    RenderUtil.DrawString(graphics, label.text, font, solidBrush, 0, 0);
                }
            }
        }

        private void DrawCapitalsAndHomeWorlds()
        {
            if (!styles.capitals.visible || (options & MapOptions.WorldsMask) == 0)
                return;
            if (resourceManager.GetXmlFileObject(@"~/res/Worlds.xml", typeof(WorldObjectCollection)) is WorldObjectCollection worlds && worlds.Worlds != null)
            {
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                solidBrush.Color = styles.capitals.textColor;
                foreach (WorldObject world in worlds.Worlds.Where(world => (world.MapOptions & options) != 0))
                {
                    world.Paint(graphics, styles.capitals.fillColor, solidBrush, styles.macroNames.SmallFont);
                }
            }
        }

        private void DrawSectorNames()
        {
            if (!(styles.showSomeSectorNames || styles.showAllSectorNames))
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;
            foreach (Sector sector in selector.Sectors
                .Where(sector => styles.showAllSectorNames || (styles.showSomeSectorNames && sector.Selected))
                .Where(sector => sector.Names.Any() || sector.Label != null))
            {
                solidBrush.Color = styles.sectorName.textColor;
                string name = sector.Label ?? sector.Names[0].Text;

                RenderUtil.DrawLabel(graphics, name, sector.Center, styles.sectorName.Font, solidBrush, styles.sectorName.textStyle);
            }
        }

        private void DrawMicroBordersFill()
        {
            if (!styles.microBorders.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;

            DrawMicroBorders(BorderLayer.Regions);

            if (styles.fillMicroBorders)
                DrawMicroBorders(BorderLayer.Fill);
        }

        private void DrawMicroBordersStroke()
        {
            if (!styles.microBorders.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;

            DrawMicroBorders(BorderLayer.Stroke);
        }

        private void DrawSubsectorNames()
        {
            if (!styles.subsectorNames.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;
            solidBrush.Color = styles.subsectorNames.textColor;
            foreach (Sector sector in selector.Sectors)
            {
                for (int i = 0; i < 16; i++)
                {
                    Subsector ss = sector.Subsector(i);
                    if (ss == null || string.IsNullOrEmpty(ss.Name))
                        continue;

                    Point center = sector.SubsectorCenter(i);
                    RenderUtil.DrawLabel(graphics, ss.Name, center, styles.subsectorNames.Font, solidBrush, styles.subsectorNames.textStyle);
                }
            }
        }

        private void DrawSubsectorGrid()
        {
            if (!styles.subsectorGrid.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighSpeed;
            const int gridSlop = 10;
            styles.subsectorGrid.pen.Apply(ref pen);

            int hmin = (int)Math.Floor(tileRect.Left / Astrometrics.SubsectorWidth) - 1 - Astrometrics.ReferenceSector.X, hmax = (int)Math.Ceiling((tileRect.Right + Astrometrics.SubsectorWidth + Astrometrics.ReferenceHex.X) / Astrometrics.SubsectorWidth);
            for (int hi = hmin; hi <= hmax; ++hi)
            {
                if (hi % 4 == 0) continue;
                float h = hi * Astrometrics.SubsectorWidth - Astrometrics.ReferenceHex.X;
                graphics.DrawLine(pen, h, tileRect.Top - gridSlop, h, tileRect.Bottom + gridSlop);
                using (graphics.Save())
                {
                    graphics.TranslateTransform(h, 0);
                    graphics.ScaleTransform(1 / Astrometrics.ParsecScaleX, 1 / Astrometrics.ParsecScaleY);
                    graphics.DrawLine(pen, 0, tileRect.Top - gridSlop, 0, tileRect.Bottom + gridSlop);
                }
            }

            int vmin = (int)Math.Floor(tileRect.Top / Astrometrics.SubsectorHeight) - 1 - Astrometrics.ReferenceSector.Y, vmax = (int)Math.Ceiling((tileRect.Bottom + Astrometrics.SubsectorHeight + Astrometrics.ReferenceHex.Y) / Astrometrics.SubsectorHeight);
            for (int vi = vmin; vi <= vmax; ++vi)
            {
                if (vi % 4 == 0) continue;
                float v = vi * Astrometrics.SubsectorHeight - Astrometrics.ReferenceHex.Y;
                graphics.DrawLine(pen, tileRect.Left - gridSlop, v, tileRect.Right + gridSlop, v);
            }
        }

        private void DrawSectorGrid()
        {
            if (!styles.sectorGrid.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighSpeed;
            const int gridSlop = 10;
            styles.sectorGrid.pen.Apply(ref pen);

            for (float h = ((float)(Math.Floor((tileRect.Left) / Astrometrics.SectorWidth) - 1) - Astrometrics.ReferenceSector.X) * Astrometrics.SectorWidth - Astrometrics.ReferenceHex.X; h <= tileRect.Right + Astrometrics.SectorWidth; h += Astrometrics.SectorWidth)
            {
                using (graphics.Save())
                {
                    graphics.TranslateTransform(h, 0);
                    graphics.ScaleTransform(1 / Astrometrics.ParsecScaleX, 1 / Astrometrics.ParsecScaleY);
                    graphics.DrawLine(pen, 0, tileRect.Top - gridSlop, 0, tileRect.Bottom + gridSlop);
                }
            }

            for (float v = ((float)(Math.Floor((tileRect.Top) / Astrometrics.SectorHeight) - 1) - Astrometrics.ReferenceSector.Y) * Astrometrics.SectorHeight - Astrometrics.ReferenceHex.Y; v <= tileRect.Bottom + Astrometrics.SectorHeight; v += Astrometrics.SectorHeight)
                graphics.DrawLine(pen, tileRect.Left - gridSlop, v, tileRect.Right + gridSlop, v);
        }

        private void DrawMacroRoutes()
        {
            if (!styles.macroRoutes.visible)
                return;

            styles.macroRoutes.pen.Apply(ref pen);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var vec in routeFiles
                .Select(file => resourceManager.GetXmlFileObject(file, typeof(VectorObject)))
                .OfType<VectorObject>()
                .Where(vec => (vec.MapOptions & options & MapOptions.BordersMask) != 0))
            {
                vec.Draw(graphics, tileRect, pen);
            }
        }

        private void DrawMacroBorders()
        {
            if (!styles.macroBorders.visible)
                return;

            styles.macroBorders.pen.Apply(ref pen);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var vec in borderFiles
                .Select(file => resourceManager.GetXmlFileObject(file, typeof(VectorObject)))
                .OfType<VectorObject>()
                .Where(vec => (vec.MapOptions & options & MapOptions.BordersMask) != 0))
            {
                vec.Draw(graphics, tileRect, pen);
            }
        }

        private void DrawRifts()
        {
            if (!styles.showRiftOverlay)
                return;

            if (styles.riftOpacity > 0f)
                graphics.DrawImageAlpha(styles.riftOpacity, s_riftImage, riftImageRect);
        }

        private void DrawGalaxyBackground()
        {
            if (!styles.showGalaxyBackground)
                return;

            if (styles.deepBackgroundOpacity > 0f && galaxyImageRect.IntersectsWith(tileRect))
            {
                AbstractImage galaxyImage = styles.lightBackground ? s_galaxyImageGray : s_galaxyImage;
                graphics.DrawImageAlpha(styles.deepBackgroundOpacity, galaxyImage, galaxyImageRect);
            }
        }

        private void DrawBackground()
        {
            graphics.SmoothingMode = SmoothingMode.HighSpeed;
            solidBrush.Color = styles.backgroundColor;
            graphics.DrawRectangle(solidBrush, tileRect);
        }

        private void InitializeImages()
        {
            Func<string, AbstractImage> prepare = (string urlPath) =>
                new AbstractImage(resourceManager.Server.MapPath("~" + urlPath), urlPath);

            lock (s_imageInitLock)
            {
                if (s_imagesInitialized)
                    return;
                s_imagesInitialized = true;

                // Actual images are loaded lazily.
                s_nebulaImage = prepare("/res/Candy/Nebula.png");
                s_riftImage = prepare("/res/Candy/Rifts.png");
                s_galaxyImage = prepare("/res/Candy/Galaxy.png");
                s_galaxyImageGray = prepare("/res/Candy/Galaxy_Gray.png");
                s_worldImages = new EasyInitConcurrentDictionary<string, AbstractImage> {
                            { "Hyd0", prepare("/res/Candy/Hyd0.png") },
                            { "Hyd1", prepare("/res/Candy/Hyd1.png") },
                            { "Hyd2", prepare("/res/Candy/Hyd2.png") },
                            { "Hyd3", prepare("/res/Candy/Hyd3.png") },
                            { "Hyd4", prepare("/res/Candy/Hyd4.png") },
                            { "Hyd5", prepare("/res/Candy/Hyd5.png") },
                            { "Hyd6", prepare("/res/Candy/Hyd6.png") },
                            { "Hyd7", prepare("/res/Candy/Hyd7.png") },
                            { "Hyd8", prepare("/res/Candy/Hyd8.png") },
                            { "Hyd9", prepare("/res/Candy/Hyd9.png") },
                            { "HydA", prepare("/res/Candy/HydA.png") },
                            { "Belt", prepare("/res/Candy/Belt.png") },
                        };
            }
        }

        private void OverlayGlyph(string glyph, Font font, Point coordinates)
        {
            PointF center = Astrometrics.HexToCenter(coordinates);
            using (graphics.Save())
            {
                graphics.TranslateTransform(center.X, center.Y);
                graphics.ScaleTransform(1 / Astrometrics.ParsecScaleX, 1 / Astrometrics.ParsecScaleY);
                graphics.DrawString(glyph, font, solidBrush, 0, 0, Graphics.StringAlignment.Centered);
            }
        }

        private void DrawMacroNames()
        {
            if (!styles.macroNames.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;

            foreach (var vec in borderFiles
            .Select(file => resourceManager.GetXmlFileObject(file, typeof(VectorObject)))
            .OfType<VectorObject>()
            .Where(vec => (vec.MapOptions & options & MapOptions.NamesMask) != 0))
            {
                bool major = vec.MapOptions.HasFlag(MapOptions.NamesMajor);
                LabelStyle labelStyle = new LabelStyle()
                {
                    Uppercase = major
                };
                Font font = major ? styles.macroNames.Font : styles.macroNames.SmallFont;
                solidBrush.Color = major ? styles.macroNames.textColor : styles.macroNames.textHighlightColor;
                vec.DrawName(graphics, tileRect, font, solidBrush, labelStyle);
            }

            foreach (var vec in riftFiles
                .Select(file => resourceManager.GetXmlFileObject(file, typeof(VectorObject)))
                .OfType<VectorObject>()
                .Where(vec => (vec.MapOptions & options & MapOptions.NamesMask) != 0))
            {
                bool major = vec.MapOptions.HasFlag(MapOptions.NamesMajor);
                LabelStyle labelStyle = new LabelStyle()
                {
                    Rotation = 35,
                    Uppercase = major
                };
                Font font = major ? styles.macroNames.Font : styles.macroNames.SmallFont;
                solidBrush.Color = major ? styles.macroNames.textColor : styles.macroNames.textHighlightColor;
                vec.DrawName(graphics, tileRect, font, solidBrush, labelStyle);
            }

            if (styles.macroRoutes.visible)
            {
                foreach (var vec in routeFiles
                    .Select(file => resourceManager.GetXmlFileObject(file, typeof(VectorObject)))
                    .OfType<VectorObject>()
                    .Where(vec => (vec.MapOptions & options & MapOptions.NamesMask) != 0))
                {
                    bool major = vec.MapOptions.HasFlag(MapOptions.NamesMajor);
                    LabelStyle labelStyle = new LabelStyle()
                    {
                        Uppercase = major
                    };
                    Font font = major ? styles.macroNames.Font : styles.macroNames.SmallFont;
                    solidBrush.Color = major ? styles.macroRoutes.textColor : styles.macroRoutes.textHighlightColor;
                    vec.DrawName(graphics, tileRect, font, solidBrush, labelStyle);
                }
            }

            if (options.HasFlag(MapOptions.NamesMinor))
            {
                Font font = styles.macroNames.MediumFont;
                solidBrush.Color = styles.macroRoutes.textHighlightColor;
                foreach (var label in minorLabels)
                {
                    using (graphics.Save())
                    {
                        graphics.TranslateTransform(label.position.X, label.position.Y);
                        graphics.ScaleTransform(1.0f / Astrometrics.ParsecScaleX, 1.0f / Astrometrics.ParsecScaleY);
                        RenderUtil.DrawString(graphics, label.text, font, solidBrush, 0, 0);
                    }
                }
            }
        }
        
        private void DrawParsecGrid()
        {
            if (!styles.parsecGrid.visible)
                return;

            graphics.SmoothingMode = SmoothingMode.HighQuality;

            const int parsecSlop = 1;

            int hx = (int)Math.Floor(tileRect.Left);
            int hw = (int)Math.Ceiling(tileRect.Width);
            int hy = (int)Math.Floor(tileRect.Top);
            int hh = (int)Math.Ceiling(tileRect.Height);

            styles.parsecGrid.pen.Apply(ref pen);

            switch (styles.hexStyle)
            {
                case HexStyle.Square:
                    for (int px = hx - parsecSlop; px < hx + hw + parsecSlop; px++)
                    {
                        float yOffset = ((px % 2) != 0) ? 0.0f : 0.5f;
                        for (int py = hy - parsecSlop; py < hy + hh + parsecSlop; py++)
                        {
                            // TODO: use RenderUtil.(Square|Hex)Edges(X|Y) arrays
                            const float inset = 0.1f;
                            graphics.DrawRectangle(pen, px + inset, py + inset + yOffset, 1 - inset * 2, 1 - inset * 2);
                        }
                    }
                    break;

                case HexStyle.Hex:
                    PointF[] points = new PointF[4];
                    for (int px = hx - parsecSlop; px < hx + hw + parsecSlop; px++)
                    {
                        float yOffset = ((px % 2) != 0) ? 0.0f : 0.5f;
                        for (int py = hy - parsecSlop; py < hy + hh + parsecSlop; py++)
                        {
                            points[0] = new PointF(px + -RenderUtil.HEX_EDGE, py + 0.5f + yOffset);
                            points[1] = new PointF(px + RenderUtil.HEX_EDGE, py + 1.0f + yOffset);
                            points[2] = new PointF(px + 1.0f - RenderUtil.HEX_EDGE, py + 1.0f + yOffset);
                            points[3] = new PointF(px + 1.0f + RenderUtil.HEX_EDGE, py + 0.5f + yOffset);
                            graphics.DrawLines(pen, points);
                        }
                    }
                    break;
                case HexStyle.None:
                    // none
                    break;
            }

            if (styles.numberAllHexes &&
                styles.worldDetails.HasFlag(WorldDetails.Hex))
            {
                solidBrush.Color = styles.hexNumber.textColor;
                for (int px = hx - parsecSlop; px < hx + hw + parsecSlop; px++)
                {
                    float yOffset = ((px % 2) != 0) ? 0.0f : 0.5f;
                    for (int py = hy - parsecSlop; py < hy + hh + parsecSlop; py++)
                    {
                        Location loc = Astrometrics.CoordinatesToLocation(px + 1, py + 1);
                        string hex;
                        switch (styles.hexCoordinateStyle)
                        {
                            default:
                            case HexCoordinateStyle.Sector: hex = loc.HexString; break;
                            case HexCoordinateStyle.Subsector: hex = loc.SubsectorHexString; break;
                        }
                        using (graphics.Save())
                        {
                            graphics.TranslateTransform(px + 0.5f, py + yOffset);
                            graphics.ScaleTransform(styles.hexContentScale / Astrometrics.ParsecScaleX, styles.hexContentScale / Astrometrics.ParsecScaleY);
                            graphics.DrawString(hex, styles.hexNumber.Font, solidBrush, 0, 0, Graphics.StringAlignment.TopCenter);
                        }
                    }
                }
            }
        }

        private void DrawPseudoRandomStars()
        {
            if (!styles.pseudoRandomStars.visible)
                return;

            // Render pseudorandom stars based on the tile # and
            // scale factor. Note that these are positioned in
            // screen space, not world space.

            //const int nStars = 75;
            int nMinStars = tileSize.Width * tileSize.Height / 300;
            int nStars = scale >= 1 ? nMinStars : (int)(nMinStars / scale);

            // NOTE: For performance's sake, three different cases are considered:
            // (1) Tile is entirely within charted space (most common) - just render
            //     the pseudorandom stars into the tile
            // (2) Tile intersects the galaxy bounds - render pseudorandom stars
            //     into a texture, then fill the galaxy vector with it
            // (3) Tile is entire outside the galaxy - don't render stars

            using (graphics.Save())
            {
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                solidBrush.Color = styles.pseudoRandomStars.fillColor;

                Random rand = new Random((((int)tileRect.Left) << 8) ^ (int)tileRect.Top);
                for (int i = 0; i < nStars; i++)
                {
                    float starX = (float)rand.NextDouble() * tileRect.Width + tileRect.X;
                    float starY = (float)rand.NextDouble() * tileRect.Height + tileRect.Y;
                    float d = (float)rand.NextDouble() * 2;

                    graphics.DrawEllipse(solidBrush, starX, starY, (float)(d / scale * Astrometrics.ParsecScaleX), (float)(d / scale * Astrometrics.ParsecScaleY));
                }
            }
        }

        private void DrawNebulaBackground()
        {
            if (!styles.showNebulaBackground)
                return;

            // Render in image-space so it scales/tiles nicely
            using (graphics.Save())
            {
                graphics.MultiplyTransform(worldSpaceToImageSpace);

                const float backgroundImageScale = 2.0f;
                const int nebulaImageWidth = 1024, nebulaImageHeight = 1024;
                // Scaled size of the background
                float w = nebulaImageWidth * backgroundImageScale;
                float h = nebulaImageHeight * backgroundImageScale;

                // Offset of the background, relative to the canvas
                float ox = (float)(-tileRect.Left * scale * Astrometrics.ParsecScaleX) % w;
                float oy = (float)(-tileRect.Top * scale * Astrometrics.ParsecScaleY) % h;
                if (ox > 0) ox -= w;
                if (oy > 0) oy -= h;

                // Number of copies needed to cover the canvas
                int nx = 1 + (int)Math.Floor(tileSize.Width / w);
                int ny = 1 + (int)Math.Floor(tileSize.Height / h);
                if (ox + nx * w < tileSize.Width) nx += 1;
                if (oy + ny * h < tileSize.Height) ny += 1;

                for (int x = 0; x < nx; ++x)
                {
                    for (int y = 0; y < ny; ++y)
                    {
                        graphics.DrawImage(s_nebulaImage, ox + x * w, oy + y * h, w + 1, h + 1);
                    }
                }
            }
        }

        private enum WorldLayer { Background, Foreground, Overlay };
        private void DrawWorld(World world, WorldLayer layer)
        {
            bool isPlaceholder = world.IsPlaceholder;
            bool isCapital = world.IsCapital;
            bool isHiPop = world.IsHi;
            bool renderName = styles.worldDetails.HasFlag(WorldDetails.AllNames) ||
                (styles.worldDetails.HasFlag(WorldDetails.KeyNames) && (isCapital || isHiPop));
            bool renderUWP = styles.worldDetails.HasFlag(WorldDetails.Uwp);

            using (graphics.Save())
            {
                AbstractPen pen = new AbstractPen(Color.Empty);
                AbstractBrush solidBrush = new AbstractBrush();

                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                // Center on the parsec
                PointF center = Astrometrics.HexToCenter(world.Coordinates);

                graphics.TranslateTransform(center.X, center.Y);
                graphics.ScaleTransform(styles.hexContentScale / Astrometrics.ParsecScaleX, styles.hexContentScale / Astrometrics.ParsecScaleY);
                graphics.RotateTransform(styles.hexRotation);

                if (layer == WorldLayer.Overlay)
                {
                    #region Population Overlay 
                    if (styles.populationOverlay.visible && world.Population > 0)
                    {
                        DrawOverlay(styles.populationOverlay, (float)Math.Sqrt(world.Population / Math.PI) * 0.00002f, ref solidBrush, ref pen);
                    }
                    #endregion

                    #region Importance Overlay
                    if (styles.importanceOverlay.visible)
                    {
                        int im = SecondSurvey.Importance(world);
                        if (im > 0)
                        {
                            DrawOverlay(styles.importanceOverlay, (im - 0.5f) * Astrometrics.ParsecScaleX, ref solidBrush, ref pen);
                        }
                    }
                    #endregion

                    #region Capital Overlay
                    if (styles.capitalOverlay.visible)
                    {
                        bool hasIm = SecondSurvey.Importance(world) >= 4;
                        bool hasCp = world.IsCapital;

                        if (hasIm && hasCp)
                            DrawOverlay(styles.capitalOverlay, 2 * Astrometrics.ParsecScaleX, ref solidBrush, ref pen);
                        else if (hasIm)
                            DrawOverlay(styles.capitalOverlayAltA, 2 * Astrometrics.ParsecScaleX, ref solidBrush, ref pen);
                        else if (hasCp)
                            DrawOverlay(styles.capitalOverlayAltB, 2 * Astrometrics.ParsecScaleX, ref solidBrush, ref pen);
                    }
                    #endregion

                    #region Highlight Worlds
                    if (styles.highlightWorlds.visible && styles.highlightWorldsPattern.Matches(world))
                    {
                        DrawOverlay(styles.highlightWorlds, Astrometrics.ParsecScaleX, ref solidBrush, ref pen);
                    }
                    #endregion
                }

                if (!styles.useWorldImages)
                {
                    // Normal (non-"Eye Candy") styles
                    if (layer == WorldLayer.Background)
                    {
                        #region Zone
                        if (styles.worldDetails.HasFlag(WorldDetails.Zone))
                        {
                            Stylesheet.StyleElement? maybeElem = ZoneStyle(world);
                            if (maybeElem?.visible ?? false)
                            {
                                Stylesheet.StyleElement elem = maybeElem.Value;
                                if (styles.showZonesAsPerimeters)
                                {
                                    using (graphics.Save())
                                    {
                                        graphics.ScaleTransform(Astrometrics.ParsecScaleX, Astrometrics.ParsecScaleY);
                                        graphics.ScaleTransform(0.95f, 0.95f);
                                        elem.pen.Apply(ref pen);
                                        graphics.DrawPath(pen, RenderUtil.HexPath);
                                    }
                                }
                                else
                                {
                                    if (!elem.fillColor.IsEmpty)
                                    {
                                        solidBrush.Color = elem.fillColor;
                                        graphics.DrawEllipse(solidBrush, -0.4f, -0.4f, 0.8f, 0.8f);
                                    }

                                    PenInfo pi = elem.pen;
                                    if (!pi.color.IsEmpty)
                                    {
                                        pi.Apply(ref pen);

                                        if (renderName && styles.fillMicroBorders)
                                        {
                                            using (graphics.Save())
                                            {
                                                graphics.IntersectClip(new RectangleF(-.5f, -.5f, 1f, renderUWP ? 0.65f : 0.75f));
                                                graphics.DrawEllipse(pen, -0.4f, -0.4f, 0.8f, 0.8f);
                                            }
                                        }
                                        else
                                        {
                                            graphics.DrawEllipse(pen, -0.4f, -0.4f, 0.8f, 0.8f);
                                        }
                                    }
                                }
                            }
                        }
                        #endregion

                        #region Hex
                        if (!styles.numberAllHexes &&
                            styles.worldDetails.HasFlag(WorldDetails.Hex))
                        {
                            string hex;
                            switch (styles.hexCoordinateStyle)
                            {
                                default:
                                case HexCoordinateStyle.Sector: hex = world.Hex; break;
                                case HexCoordinateStyle.Subsector: hex = world.SubsectorHex; break;
                            }
                            solidBrush.Color = styles.hexNumber.textColor;
                            graphics.DrawString(hex, styles.hexNumber.Font, solidBrush, 
                                styles.hexNumber.position.X, 
                                styles.hexNumber.position.Y, Graphics.StringAlignment.TopCenter);
                        }
                        #endregion
                    }

                    if (layer == WorldLayer.Foreground)
                    {
                        Stylesheet.StyleElement? elem = ZoneStyle(world);
                        TextBackgroundStyle worldTextBackgroundStyle = (!elem?.fillColor.IsEmpty ?? false)
                            ? TextBackgroundStyle.None : styles.worlds.textBackgroundStyle;

                        if (!isPlaceholder)
                        {
                            #region GasGiant
                            if (styles.worldDetails.HasFlag(WorldDetails.GasGiant) && world.GasGiants > 0) {
                                DrawGasGiant(
                                    styles.worlds.textColor, 
                                    styles.GasGiantPosition.X, 
                                    styles.GasGiantPosition.Y, 
                                    0.05f, 
                                    styles.showGasGiantRing);
                            }
                            #endregion

                            #region Starport
                            if (styles.worldDetails.HasFlag(WorldDetails.Starport))
                            {
                                string starport = world.Starport.ToString();
                                if (styles.showTL)
                                    starport += "-" + SecondSurvey.ToHex(world.TechLevel);
                                DrawWorldLabel(worldTextBackgroundStyle, solidBrush, styles.worlds.textColor, styles.starport.position, styles.starport.Font, starport);
                            }
                            #endregion

                            #region UWP
                            if (renderUWP)
                            {
                                solidBrush.Color = styles.uwp.fillColor;
                                DrawWorldLabel(styles.uwp.textBackgroundStyle, solidBrush, styles.uwp.textColor, styles.uwp.position, styles.hexNumber.Font, world.UWP);
                            }
                            #endregion

                            #region Bases
                            // TODO: Mask off background for glyphs
                            if (styles.worldDetails.HasFlag(WorldDetails.Bases))
                            {
                                string bases = world.Bases;

                                // Special case: Show Zho Naval+Military as diamond
                                if (world.BaseAllegiance == "Zh" && bases == "KM")
                                    bases = "Z";

                                // Base 1
                                bool bottomUsed = false;
                                if (bases.Length > 0)
                                {
                                    Glyph glyph = Glyph.FromBaseCode(world.BaseAllegiance, bases[0]);
                                    if (glyph.IsPrintable)
                                    {
                                        PointF pt = styles.BaseTopPosition;
                                        if (glyph.Bias == Glyph.GlyphBias.Bottom && !styles.ignoreBaseBias)
                                        {
                                            pt = styles.BaseBottomPosition;
                                            bottomUsed = true;
                                        }

                                        solidBrush.Color = glyph.IsHighlighted ? styles.worlds.textHighlightColor : styles.worlds.textColor;
                                        RenderUtil.DrawGlyph(graphics, glyph, fonts, solidBrush, pt);
                                    }
                                }

                                // Base 2
                                if (bases.Length > 1)
                                {
                                    Glyph glyph = Glyph.FromBaseCode(world.LegacyAllegiance, bases[1]);
                                    if (glyph.IsPrintable)
                                    {
                                        PointF pt = bottomUsed ? styles.BaseTopPosition : styles.BaseBottomPosition;
                                        solidBrush.Color = glyph.IsHighlighted ? styles.worlds.textHighlightColor : styles.worlds.textColor;
                                        RenderUtil.DrawGlyph(graphics, glyph, fonts, solidBrush, pt);
                                    }
                                }

                                // Research Stations
                                {
                                    string rs;
                                    Glyph? glyph = null;
                                    if ((rs = world.ResearchStation) != null)
                                    {
                                        glyph = Glyph.FromResearchCode(rs);
                                    }
                                    else if (world.IsReserve)
                                    {
                                        glyph = Glyph.Reserve;
                                    }
                                    else if (world.IsPenalColony)
                                    {
                                        glyph = Glyph.Prison;
                                    }
                                    else if (world.IsPrisonExileCamp)
                                    {
                                        glyph = Glyph.ExileCamp;
                                    }
                                    if (glyph.HasValue)
                                    {
                                        solidBrush.Color = glyph.Value.IsHighlighted ? styles.worlds.textHighlightColor : styles.worlds.textColor;
                                        RenderUtil.DrawGlyph(graphics, glyph.Value, fonts, solidBrush, styles.BaseMiddlePosition);
                                    }
                                }
                            }
                            #endregion
                        }

                        #region Disc
                        if (styles.worldDetails.HasFlag(WorldDetails.Type))
                        {
                            if (isPlaceholder)
                            {
                                var e = world.IsAnomaly ? styles.anomaly : styles.placeholder;
                                DrawWorldLabel(e.textBackgroundStyle, solidBrush, e.textColor, e.position, e.Font, e.content);
                            }
                            else
                            {
                                using (graphics.Save())
                                {
                                    graphics.TranslateTransform(styles.DiscPosition.X, styles.DiscPosition.Y);
                                    if (world.Size <= 0)
                                    {
                                        #region Asteroid-Belt
                                        if (styles.worldDetails.HasFlag(WorldDetails.Asteroids))
                                        {
                                            // Basic pattern, with probability varying per position:
                                            //   o o o
                                            //  o o o o
                                            //   o o o

                                            int[] lpx = { -2, 0, 2, -3, -1, 1, 3, -2, 0, 2 };
                                            int[] lpy = { -2, -2, -2, 0, 0, 0, 0, 2, 2, 2 };
                                            float[] lpr = { 0.5f, 0.9f, 0.5f, 0.6f, 0.9f, 0.9f, 0.6f, 0.5f, 0.9f, 0.5f };

                                            solidBrush.Color = styles.worlds.textColor;

                                            // Random generator is seeded with world location so it is always the same
                                            Random rand = new Random(world.Coordinates.X ^ world.Coordinates.Y);
                                            for (int i = 0; i < lpx.Length; ++i)
                                            {
                                                if (rand.NextDouble() < lpr[i])
                                                {
                                                    float px = lpx[i] * 0.035f;
                                                    float py = lpy[i] * 0.035f;

                                                    float w = 0.04f + (float)rand.NextDouble() * 0.03f;
                                                    float h = 0.04f + (float)rand.NextDouble() * 0.03f;

                                                    // If necessary, add jitter here
                                                    float dx = 0, dy = 0;

                                                    graphics.DrawEllipse(solidBrush,
                                                        px + dx - w / 2, py + dy - h / 2, w, h);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            // Just a glyph
                                            solidBrush.Color = styles.worlds.textColor;
                                            RenderUtil.DrawGlyph(graphics, Glyph.DiamondX, fonts, solidBrush, new PointF(0,0));
                                        }
                                        #endregion
                                    }
                                    else
                                    {
                                        styles.WorldColors(world, out Color penColor, out Color brushColor);

                                        if (!brushColor.IsEmpty && !penColor.IsEmpty)
                                        {
                                            solidBrush.Color = brushColor;
                                            styles.worldWater.pen.Apply(ref pen);
                                            pen.Color = penColor;
                                            graphics.DrawEllipse(pen, solidBrush, -styles.discRadius, -styles.discRadius, 2 * styles.discRadius, 2 * styles.discRadius);
                                        }
                                        else if (!brushColor.IsEmpty)
                                        {
                                            solidBrush.Color = brushColor;
                                            graphics.DrawEllipse(solidBrush, -styles.discRadius, -styles.discRadius, 2 * styles.discRadius, 2 * styles.discRadius);
                                        }
                                        else if (!penColor.IsEmpty)
                                        {
                                            styles.worldWater.pen.Apply(ref pen);
                                            pen.Color = penColor;
                                            graphics.DrawEllipse(pen, -styles.discRadius, -styles.discRadius, 2 * styles.discRadius, 2 * styles.discRadius);
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Dotmap
                            if (!world.IsAnomaly)
                            {
                                solidBrush.Color = styles.worlds.textColor;
                                graphics.DrawEllipse(solidBrush, -styles.discRadius, -styles.discRadius, 2 * styles.discRadius, 2 * styles.discRadius);
                            }
                        }
                        #endregion

                        #region Name
                        if (renderName)
                        {
                            string name = world.Name;
                            if ((isHiPop && styles.worldDetails.HasFlag(WorldDetails.Highlight)) || styles.worlds.textStyle.Uppercase)
                                name = name.ToUpperInvariant();

                            Color textColor = (isCapital && styles.worldDetails.HasFlag(WorldDetails.Highlight))
                                ? styles.worlds.textHighlightColor : styles.worlds.textColor;
                            Font font = ((isHiPop || isCapital) && styles.worldDetails.HasFlag(WorldDetails.Highlight))
                                ? styles.worlds.LargeFont : styles.worlds.Font;

                            DrawWorldLabel(worldTextBackgroundStyle, solidBrush, textColor, styles.worlds.textStyle.Translation, font, name);
                        }
                        #endregion

                        #region Allegiance
                        // TODO: Mask off background for allegiance
                        if (styles.worldDetails.HasFlag(WorldDetails.Allegiance))
                        {
                            string alleg = world.Allegiance;
                            if (!SecondSurvey.IsDefaultAllegiance(alleg))
                            {
                                if (!styles.t5AllegianceCodes && alleg.Length > 2)
                                    alleg = SecondSurvey.T5AllegianceCodeToLegacyCode(alleg);

                                solidBrush.Color = styles.worlds.textColor;

                                if (styles.lowerCaseAllegiance)
                                    alleg = alleg.ToLowerInvariant();

                                graphics.DrawString(alleg, styles.worlds.SmallFont, solidBrush, styles.AllegiancePosition.X, styles.AllegiancePosition.Y, Graphics.StringAlignment.Centered);
                            }
                        }
                        #endregion
                    }
                }
                else // styles.useWorldImages
                {
                    // "Eye-Candy" style

                    float imageRadius = ((world.Size <= 0) ? 0.6f : (0.3f * (world.Size / 5.0f + 0.2f))) / 2;
                    float decorationRadius = imageRadius;

                    if (layer == WorldLayer.Background)
                    {
                        #region Disc
                        if (styles.worldDetails.HasFlag(WorldDetails.Type))
                        {
                            if (isPlaceholder)
                            {
                                var e = world.IsAnomaly ? styles.anomaly : styles.placeholder;
                                DrawWorldLabel(e.textBackgroundStyle, solidBrush, e.textColor, e.position, e.Font, e.content);
                            }
                            else if (world.Size <= 0)
                            {
                                const float scaleX = 1.5f;
                                const float scaleY = 1.0f;
                                AbstractImage img = s_worldImages["Belt"];

                                graphics.DrawImage(img, -imageRadius * scaleX, -imageRadius * scaleY, imageRadius * 2 * scaleX, imageRadius * 2 * scaleY);
                            }
                            else
                            {
                                AbstractImage img;
                                switch (world.Hydrographics)
                                {
                                    default:
                                    case 0x0: img = s_worldImages["Hyd0"]; break;
                                    case 0x1: img = s_worldImages["Hyd1"]; break;
                                    case 0x2: img = s_worldImages["Hyd2"]; break;
                                    case 0x3: img = s_worldImages["Hyd3"]; break;
                                    case 0x4: img = s_worldImages["Hyd4"]; break;
                                    case 0x5: img = s_worldImages["Hyd5"]; break;
                                    case 0x6: img = s_worldImages["Hyd6"]; break;
                                    case 0x7: img = s_worldImages["Hyd7"]; break;
                                    case 0x8: img = s_worldImages["Hyd8"]; break;
                                    case 0x9: img = s_worldImages["Hyd9"]; break;
                                    case 0xA: img = s_worldImages["HydA"]; break;
                                }
                                if (img != null)
                                {
                                    graphics.DrawImage(img, -imageRadius, -imageRadius, imageRadius * 2, imageRadius * 2);
                                }
                            }
                        }
                        else
                        {
                            // Dotmap
                            if (!world.IsAnomaly)
                            {
                                solidBrush.Color = styles.worlds.textColor;
                                graphics.DrawEllipse(solidBrush, -styles.discRadius, -styles.discRadius, 2 * styles.discRadius, 2 * styles.discRadius);
                            }
                        }
                        #endregion
                    }

                    if (isPlaceholder)
                        return;

                    if (layer == WorldLayer.Foreground)
                    {
                        decorationRadius += 0.1f;

                        #region Zone
                        if (styles.worldDetails.HasFlag(WorldDetails.Zone))
                        {
                            if (world.IsAmber || world.IsRed)
                            {
                                PenInfo pi = world.IsAmber ? styles.amberZone.pen : styles.redZone.pen;
                                pi.Apply(ref pen);

                                graphics.DrawArc(pen, -decorationRadius, -decorationRadius, decorationRadius * 2, decorationRadius * 2, 5, 80);
                                graphics.DrawArc(pen, -decorationRadius, -decorationRadius, decorationRadius * 2, decorationRadius * 2, 95, 80);
                                graphics.DrawArc(pen, -decorationRadius, -decorationRadius, decorationRadius * 2, decorationRadius * 2, 185, 80);
                                graphics.DrawArc(pen, -decorationRadius, -decorationRadius, decorationRadius * 2, decorationRadius * 2, 275, 80);
                                decorationRadius += 0.1f;
                            }
                        }
                        #endregion

                        #region GasGiant
                        if (styles.worldDetails.HasFlag(WorldDetails.GasGiant) && world.GasGiants > 0)
                        {
                            const float symbolRadius = 0.05f;
                            if (styles.showGasGiantRing)
                                decorationRadius += symbolRadius;
                            DrawGasGiant(
                                styles.worlds.textHighlightColor,
                                decorationRadius, 0, symbolRadius,
                                styles.showGasGiantRing);
                            decorationRadius += 0.1f;
                        }
                        #endregion

                        #region UWP
                        if (renderUWP)
                        {
                            solidBrush.Color = styles.worlds.textColor;
                            // TODO: Scale, like the name text.
                            graphics.DrawString(world.UWP, styles.hexNumber.Font, solidBrush, decorationRadius, styles.uwp.position.Y, Graphics.StringAlignment.CenterLeft);
                        }
                        #endregion

                        #region Name
                        if (renderName)
                        {
                            string name = world.Name;
                            if (isHiPop)
                                name = name.ToUpperInvariant();

                            using (graphics.Save())
                            {
                                Color textColor = (isCapital && styles.worldDetails.HasFlag(WorldDetails.Highlight))
                                    ? styles.worlds.textHighlightColor : styles.worlds.textColor;

                                if (styles.worlds.textStyle.Uppercase)
                                    name = name.ToUpper();

                                graphics.TranslateTransform(decorationRadius, 0.0f);
                                graphics.ScaleTransform(styles.worlds.textStyle.Scale.Width, styles.worlds.textStyle.Scale.Height);
                                graphics.TranslateTransform(graphics.MeasureString(name, styles.worlds.Font).Width / 2, 0.0f); // Left align

                                DrawWorldLabel(styles.worlds.textBackgroundStyle, solidBrush, textColor, styles.worlds.textStyle.Translation, styles.worlds.Font, name);
                            }
                        }
                        #endregion
                    }
                }
            }
        }

        private void DrawStars(World world)
        {
            using (graphics.Save())
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                PointF center = Astrometrics.HexToCenter(world.Coordinates);

                graphics.TranslateTransform(center.X, center.Y);
                graphics.ScaleTransform(styles.hexContentScale / Astrometrics.ParsecScaleX, styles.hexContentScale / Astrometrics.ParsecScaleY);

                int i = 0;
                foreach (var props in 
                    StellarData.Parse(world.Stellar).Select(s => StellarRendering.StarToProps(s)).OrderByDescending(p => p.radius)) {
                    solidBrush.Color = props.color;
                    pen.Color = props.borderColor;
                    pen.DashStyle = Graphics.DashStyle.Solid;
                    pen.Width = styles.worlds.pen.width;
                    PointF offset = StellarRendering.Offset(i++);
                    const float offsetScale = 0.3f;
                    float r = 0.15f * props.radius;
                    graphics.DrawEllipse(pen, solidBrush, offset.X * offsetScale - r, offset.Y * offsetScale - r, r*2, r*2);
                }
            }
        }

        private void DrawGasGiant(Color color, float x, float y, float r, bool ring)
        {
            using (graphics.Save())
            {
                graphics.TranslateTransform(x, y);
                solidBrush.Color = color;
                graphics.DrawEllipse(solidBrush,
                    - r,
                    - r,
                    r * 2,
                    r * 2);

                if (ring)
                {
                    graphics.RotateTransform(-30);
                    pen.Color = color;
                    pen.Width = r / 4;
                    pen.DashStyle = Graphics.DashStyle.Solid;
                    pen.CustomDashPattern = null;
                    graphics.DrawEllipse(pen,
                        -r * 1.75f,
                        -r * 0.4f,
                        r * 1.75f * 2,
                        r * 0.4f * 2);
                }
            }
        }

        private Stylesheet.StyleElement? ZoneStyle(World world)
        {
            if (world.IsAmber)
                return styles.amberZone;
            if (world.IsRed)
                return styles.redZone;
            if (styles.greenZone.visible)
                return styles.greenZone;
            return null;
        }

        private void DrawWorldLabel(TextBackgroundStyle backgroundStyle, AbstractBrush brush, Color color, PointF position, Font font, string text)
        {
            var size = graphics.MeasureString(text, font);

            switch (backgroundStyle)
            {
                case TextBackgroundStyle.None:
                    break;

                default:
                case TextBackgroundStyle.Rectangle:
                    if (!styles.fillMicroBorders)
                    {
                        // TODO: Implement this with a clipping region instead
                        brush.Color = styles.backgroundColor;
                        graphics.DrawRectangle(brush, position.X - size.Width / 2, position.Y - size.Height / 2, size.Width, size.Height);
                    }
                    break;

                case TextBackgroundStyle.Filled:
                    graphics.DrawRectangle(brush, position.X - size.Width / 2, position.Y - size.Height / 2, size.Width, size.Height);
                    break;

                case TextBackgroundStyle.Outline:
                case TextBackgroundStyle.Shadow:
                    {
                        // TODO: These scaling factors are constant for a render; compute once

                        // Invert the current scaling transforms
                        float sx = 1.0f / styles.hexContentScale;
                        float sy = 1.0f / styles.hexContentScale;
                        sx *= Astrometrics.ParsecScaleX;
                        sy *= Astrometrics.ParsecScaleY;
                        sx /= (float)scale * Astrometrics.ParsecScaleX;
                        sy /= (float)scale * Astrometrics.ParsecScaleY;

                        const int outlineSize = 2;
                        const int outlineSkip = 1;

                        int outlineStart = backgroundStyle == TextBackgroundStyle.Outline
                            ? -outlineSize
                            : 0;

                        brush.Color = styles.backgroundColor;

                        for (int dx = outlineStart; dx <= outlineSize; dx += outlineSkip)
                        {
                            for (int dy = outlineStart; dy <= outlineSize; dy += outlineSkip)
                            {
                                graphics.DrawString(text, font, brush, position.X + sx * dx, position.Y + sy * dy, Graphics.StringAlignment.Centered);
                            }
                        }
                        break;
                    }
            }

            brush.Color = color;
            graphics.DrawString(text, font, brush, position.X, position.Y, Graphics.StringAlignment.Centered);
        }

        private static readonly Regex WRAP_REGEX = new Regex(@"\s+(?![a-z])");

        private void DrawMicroLabels()
        {
            if (!styles.showMicroNames)
                return;

            using (graphics.Save())
            {
                AbstractBrush solidBrush = new AbstractBrush();

                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                foreach (Sector sector in selector.Sectors)
                {
                    solidBrush.Color = styles.microBorders.textColor;
                    foreach (Border border in sector.BordersAndRegions.Where(border => border.ShowLabel))
                    {
                        string label = border.GetLabel(sector);
                        if (label == null)
                            continue;
                        Hex labelHex = border.LabelPosition;
                        PointF labelPos = Astrometrics.HexToCenter(Astrometrics.LocationToCoordinates(new Location(sector.Location, labelHex)));
                        // TODO: Replace these with, well, positions!
                        //labelPos.X -= 0.5f;
                        //labelPos.Y -= 0.5f;

                        if (border.WrapLabel)
                            label = WRAP_REGEX.Replace(label, "\n");

                        RenderUtil.DrawLabel(graphics, label, labelPos, styles.microBorders.Font, solidBrush, styles.microBorders.textStyle);
                    }                 

                    foreach (Label label in sector.Labels)
                    {
                        string text = label.Text;
                        if (label.Wrap)
                            text = WRAP_REGEX.Replace(text, "\n");

                        PointF labelPos = Astrometrics.HexToCenter(Astrometrics.LocationToCoordinates(new Location(sector.Location, label.Hex)));
                        // TODO: Adopt some of the tweaks from .MSEC
                        labelPos.Y -= label.OffsetY * 0.7f;

                        Font font;
                        switch (label.Size)
                        {
                            case "small": font = styles.microBorders.SmallFont; break;
                            case "large": font = styles.microBorders.LargeFont; break;
                            default: font = styles.microBorders.Font; break;
                        }

                        if (!styles.grayscale &&
                            ColorUtil.NoticeableDifference(label.Color, styles.backgroundColor) &&
                            (label.Color != Label.DefaultColor))
                            solidBrush.Color = label.Color;
                        else
                            solidBrush.Color = styles.microBorders.textColor;
                        RenderUtil.DrawLabel(graphics, text, labelPos, font, solidBrush, styles.microBorders.textStyle);
                    }
                }
            }
        }
        
        private void DrawMicroRoutes()
        {
            if (!styles.microRoutes.visible)
                return;

            using (graphics.Save())
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                AbstractPen pen = new AbstractPen(Color.Empty);
                styles.microRoutes.pen.Apply(ref pen);
                float baseWidth = styles.microRoutes.pen.width;

                foreach (var tuple in selector.Routes)
                {
                    Sector sector = tuple.Item1;
                    Route route = tuple.Item2;
                    // Compute source/target sectors (may be offset)
                    sector.RouteToStartEnd(route, out Location startLocation, out Location endLocation);
                    if (startLocation == endLocation)
                        continue;

                    // If drawing dashed lines twice and the start/end are swapped the
                    // dashes don't overlap correctly. So "sort" the points.
                    if (startLocation > endLocation)
                        Util.Swap(ref startLocation, ref endLocation);

                    PointF startPoint = Astrometrics.HexToCenter(Astrometrics.LocationToCoordinates(startLocation));
                    PointF endPoint = Astrometrics.HexToCenter(Astrometrics.LocationToCoordinates(endLocation));

                    // Shorten line to leave room for world glyph
                    OffsetSegment(ref startPoint, ref endPoint, 0.25f);

                    float? routeWidth = route.Width;
                    Color? routeColor = route.Color;
                    LineStyle? routeStyle = styles.overrideLineStyle ?? route.Style;

                    SectorStylesheet.StyleResult ssr = sector.ApplyStylesheet("route", route.Allegiance ?? route.Type ?? "Im");
                    routeStyle = routeStyle ?? ssr.GetEnum<LineStyle>("style");
                    routeColor = routeColor ?? ssr.GetColor("color");
                    routeWidth = routeWidth ?? (float?)ssr.GetNumber("width") ?? 1.0f;

                    // In grayscale, convert default color and style to non-default style
                    if (styles.grayscale && !routeColor.HasValue && !routeStyle.HasValue)
                        routeStyle = LineStyle.Dashed;

                    routeColor = routeColor ?? styles.microRoutes.pen.color;
                    routeStyle = routeStyle ?? LineStyle.Solid;

                    // Ensure color is visible
                    if (styles.grayscale || !ColorUtil.NoticeableDifference(routeColor.Value, styles.backgroundColor))
                        routeColor = styles.microRoutes.pen.color; // default

                    if (routeStyle.Value == LineStyle.None)
                        continue;

                    pen.Color = routeColor.Value;
                    pen.Width = routeWidth.Value * baseWidth;
                    pen.DashStyle = LineStyleToDashStyle(routeStyle.Value);

                    graphics.DrawLine(pen, startPoint, endPoint);
                }
            }
        }

        private static void OffsetSegment(ref PointF startPoint, ref PointF endPoint, float offset)
        {
            float dx = endPoint.X - startPoint.X;
            float dy = endPoint.Y - startPoint.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            float ddx = dx * offset / length;
            float ddy = dy * offset / length;
            startPoint.X += ddx;
            startPoint.Y += ddy;
            endPoint.X -= ddx;
            endPoint.Y -= ddy;
        }

        private void DrawOverlay(Stylesheet.StyleElement elem, float r, ref AbstractBrush solidBrush, ref AbstractPen pen)
        {
            // Prevent "Out of memory" exception when rendering to GDI+.
            if (r < 0.001f)
                return;
            if (!elem.fillColor.IsEmpty && !elem.pen.color.IsEmpty)
            {
                solidBrush.Color = elem.fillColor;
                elem.pen.Apply(ref pen);
                graphics.DrawEllipse(pen, solidBrush, -r, -r, r * 2, r * 2);
            }
            else if (!elem.fillColor.IsEmpty)
            {
                solidBrush.Color = elem.fillColor;
                graphics.DrawEllipse(solidBrush, -r, -r, r * 2, r * 2);
            }
            else if (!elem.pen.color.IsEmpty)
            {
                elem.pen.Apply(ref pen);
                graphics.DrawEllipse(pen, -r, -r, r * 2, r * 2);
            }
        }

        private static Graphics.DashStyle LineStyleToDashStyle(LineStyle style)
        {
            switch (style)
            {
                default:
                case LineStyle.Solid: return Graphics.DashStyle.Solid;
                case LineStyle.Dashed: return Graphics.DashStyle.Dash;
                case LineStyle.Dotted: return Graphics.DashStyle.Dot;
                case LineStyle.None: throw new ApplicationException("LineStyle.None should be detected earlier");
            }
        }
        
        private enum BorderLayer { Fill, Stroke, Regions };
        private void DrawMicroBorders(BorderLayer layer)
        {
            const byte FILL_ALPHA = 64;

            PathUtil.PathType borderPathType = styles.microBorderStyle == MicroBorderStyle.Square ?
                PathUtil.PathType.Square : PathUtil.PathType.Hex;
            RenderUtil.HexEdges(borderPathType, out float[] edgex, out float[] edgey);

            AbstractBrush solidBrush = new AbstractBrush();
            AbstractPen pen = new AbstractPen(Color.Empty);
            styles.microBorders.pen.Apply(ref pen);

            foreach (Sector sector in selector.Sectors)
            {
                AbstractPath sectorClipPath = null;

                using (graphics.Save())
                {
                    // This looks craptacular for Candy style borders :(
                    if (ClipOutsectorBorders &&
                        (layer == BorderLayer.Fill || styles.microBorderStyle != MicroBorderStyle.Curve))
                    {
                        Sector.ClipPath clip = sector.ComputeClipPath(borderPathType);
                        if (!tileRect.IntersectsWith(clip.bounds))
                            continue;

                        sectorClipPath = new AbstractPath(clip.clipPathPoints, clip.clipPathPointTypes);
                        if (sectorClipPath != null)
                            graphics.IntersectClip(sectorClipPath);
                    }

                    graphics.SmoothingMode = SmoothingMode.AntiAlias;

                    foreach (Border border in (layer == BorderLayer.Regions ? (IEnumerable<Border>)sector.Regions : sector.Borders))
                    {
                        BorderPath borderPath = border.ComputeGraphicsPath(sector, borderPathType);

                        AbstractPath drawPath = new AbstractPath(borderPath.points, borderPath.types);

                        Color? borderColor = border.Color;
                        LineStyle? borderStyle = border.Style;

                        SectorStylesheet.StyleResult ssr = sector.ApplyStylesheet("border", border.Allegiance);
                        borderStyle = borderStyle ?? ssr.GetEnum<LineStyle>("style") ?? LineStyle.Solid;
                        borderColor = borderColor ?? ssr.GetColor("color") ?? styles.microBorders.pen.color;

                        if (layer == BorderLayer.Stroke && borderStyle.Value == LineStyle.None)
                            continue;

                        if (styles.grayscale ||
                            !ColorUtil.NoticeableDifference(borderColor.Value, styles.backgroundColor))
                        {
                            borderColor = styles.microBorders.pen.color; // default
                        }

                        pen.Color = borderColor.Value;
                        pen.DashStyle = LineStyleToDashStyle(borderStyle.Value);

                        // Allow style to override
                        if (styles.microBorders.pen.dashStyle != Graphics.DashStyle.Solid)
                            pen.DashStyle = styles.microBorders.pen.dashStyle;

                        if (styles.microBorderStyle != MicroBorderStyle.Curve)
                        {
                            // Clip to the path itself - this means adjacent borders don't clash
                            using (graphics.Save())
                            {
                                graphics.IntersectClip(drawPath);
                                switch (layer)
                                {
                                    case BorderLayer.Regions:
                                    case BorderLayer.Fill:
                                        solidBrush.Color = Color.FromArgb(FILL_ALPHA, borderColor.Value);
                                        graphics.DrawPath(solidBrush, drawPath);
                                        break;
                                    case BorderLayer.Stroke:
                                        graphics.DrawPath(pen, drawPath);
                                        break;
                                }
                            }
                        }
                        else
                        {
                            switch (layer)
                            {
                                case BorderLayer.Regions:
                                case BorderLayer.Fill:
                                    solidBrush.Color = Color.FromArgb(FILL_ALPHA, borderColor.Value);
                                    graphics.DrawClosedCurve(solidBrush, borderPath.points);
                                    break;

                                case BorderLayer.Stroke:
                                    foreach (var segment in borderPath.curves)
                                    {
                                        if (segment.closed)
                                            graphics.DrawClosedCurve(pen, segment.points, 0.6f);
                                        else
                                            graphics.DrawCurve(pen, segment.points, 0.6f);
                                    }
                                    break;
                            }
                        }
                    }
                }
            }
        }
    }
}
