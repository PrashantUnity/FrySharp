using Material.Icons;
using PdfEditorApp.Plugins.CSharpEditor.Models;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Documentation;

public partial class DocumentationService
{
    private DocCategory BuildDisplayApisCategory()
    {
        return new DocCategory
        {
            Id = "display_apis",
            Title = "Interactive Display & .Dump()",
            IconKind = MaterialIconKind.TelevisionGuide,
            AccentColor = "#E3B341",
            Badge = "Output APIs",
            Description = "Rich visual outputs: interactive tables, object inspectors, HTML, Markdown, images, and live animations.",
            Articles = new List<DocArticle>
            {
                CreateDumpApiArticle(),
                CreateMultiLanguageDisplayArticle(),
                CreateTableAndInspectorArticle(),
                CreateHtmlAndMarkdownArticle(),
                CreateImageDisplayArticle(),
                CreateAnimateAndCancellationArticle(),
                CreateChartsQuickStartArticle(),
                CreateInteractive3DVisualizationArticle()
            }
        };
    }

    private DocCategory BuildShortcutsCategory()
    {
        return new DocCategory
        {
            Id = "shortcuts_category",
            Title = "Keyboard Shortcuts",
            IconKind = MaterialIconKind.KeyboardOutline,
            AccentColor = "#F97316",
            Badge = "Keybindings",
            Description = "Visual Studio Code keyboard shortcuts reference for macOS and Windows/Linux.",
            Articles = new List<DocArticle>
            {
                CreateShortcutsReferenceArticle()
            }
        };
    }

    private DocArticle CreateDumpApiArticle()
    {
        return new DocArticle
        {
            Id = "dump_api",
            Title = "The .Dump() Extension Method",
            Subtitle = "Inspect any variable, array, collection, or object in the interactive results deck.",
            ReadingTime = "3 min read",
            Summary = ".Dump() is the primary exploratory method in FrySharp. It turns any C# object into an interactive visual table and prints structured representation to the console.",
            Keywords = new List<string> { "dump", "table", "inspect", "linq", "console", "results" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Usage of .Dump()",
                    Content = "Simply call .Dump() on any expression. You can supply an optional string label to organize multiple outputs:",
                    CalloutType = DocCalloutType.Tip,
                    CalloutText = ".Dump() returns the original object, so it can be inserted inline into LINQ method chains without altering the pipeline!"
                }
            },
            ApiSignatures = new List<DocApiSignature>
            {
                new()
                {
                    MethodName = "obj.Dump<T>",
                    ReturnType = "T",
                    Parameters = "this T obj, string? label = null",
                    Description = "Renders an interactive visual table in the Results dock tab and writes formatted representation to console. Returns obj."
                }
            },
            CodeSnippets = new List<DocCodeSnippet>
            {
                new()
                {
                    Id = "snip_dump_linq",
                    Title = "Inline .Dump() in LINQ Pipeline",
                    Description = "Inspect intermediate and final query results with inline labels.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        var topUsers = new[]
                        {
                            new { Id = 1, Name = "Alice Smith", Role = "Admin", Score = 98 },
                            new { Id = 2, Name = "Bob Jones", Role = "User", Score = 84 },
                            new { Id = 3, Name = "Carol White", Role = "Manager", Score = 92 },
                            new { Id = 4, Name = "David Brown", Role = "User", Score = 76 }
                        }
                        .Where(u => u.Score >= 80).Dump("Filtered High Scorers")
                        .OrderByDescending(u => u.Score)
                        .ToList().Dump("Sorted Final Ranking");
                        """
                }
            }
        };
    }

    private DocArticle CreateTableAndInspectorArticle()
    {
        return new DocArticle
        {
            Id = "table_and_inspector",
            Title = "Tables & Object Inspector",
            Subtitle = "Interactive DataGrid tables and hierarchical object property trees.",
            ReadingTime = "3 min read",
            Summary = "Display.Table and Display.Inspector produce rich, interactive UI controls in the Bottom Tool Deck.",
            Keywords = new List<string> { "table", "datagrid", "inspector", "reflection", "properties" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Interactive Data Tables (Display.Table)",
                    Content = "Display.Table creates a high-performance DataGrid with sortable column headers, search filtering, and row count metrics. It accepts any IEnumerable or dictionary."
                },
                new()
                {
                    Heading = "Object Tree Inspector (Display.Inspector)",
                    Content = "Display.Inspector generates an expandable tree showing all public and private properties, field values, and internal nested objects for deep debugging."
                }
            },
            CodeSnippets = new List<DocCodeSnippet>
            {
                new()
                {
                    Id = "snip_table_demo",
                    Title = "Display.Table with Custom Data",
                    Description = "Generate an interactive data table with custom columns.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        var inventory = new[]
                        {
                            new { SKU = "PROD-101", Name = "Wireless Mouse", Price = 29.99, Stock = 145 },
                            new { SKU = "PROD-102", Name = "Mechanical Keyboard", Price = 89.99, Stock = 42 },
                            new { SKU = "PROD-103", Name = "USB-C Dock", Price = 119.50, Stock = 18 }
                        };

                        Display.Table(inventory, label: "Warehouse Stock Inventory");
                        """
                }
            }
        };
    }

    private DocArticle CreateHtmlAndMarkdownArticle()
    {
        return new DocArticle
        {
            Id = "html_and_markdown",
            Title = "HTML & Markdown Rendering",
            Subtitle = "Emit styled HTML cards, SVG vectors, and formatted Markdown.",
            ReadingTime = "3 min read",
            Summary = "Use Display.Html and Display.Markdown to output rich web-style documentation, badges, and vector charts.",
            Keywords = new List<string> { "html", "markdown", "svg", "css", "styling", "cards" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Rich HTML Output (Display.Html)",
                    Content = "Render styled markup with inline CSS, SVG charts, and custom formatting:",
                    CalloutType = DocCalloutType.Tip,
                    CalloutText = "HTML outputs render safely inside the bottom results panel and notebook output regions."
                }
            },
            CodeSnippets = new List<DocCodeSnippet>
            {
                new()
                {
                    Id = "snip_html_card",
                    Title = "Render Styled HTML Card",
                    Description = "Create a modern visual status banner with HTML and inline CSS.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """"
                        Display.Html("""
                        <div style='background: #0d1117; border: 1px solid #30363d; border-radius: 8px; padding: 16px; color: #c9d1d9; font-family: sans-serif;'>
                            <h3 style='margin: 0 0 8px 0; color: #58a6ff;'>🚀 Migration Job Succeeded</h3>
                            <p style='margin: 0;'>Processed <b>1,420</b> PDF document pages in <b>214 ms</b>.</p>
                            <div style='margin-top: 10px; font-size: 12px; color: #8b949e;'>Engine: Roslyn .NET 10 • Status: OK</div>
                        </div>
                        """);
                        """"
                }
            }
        };
    }

    private DocArticle CreateImageDisplayArticle()
    {
        return new DocArticle
        {
            Id = "image_display",
            Title = "Displaying Images & Bitmaps",
            Subtitle = "Render Avalonia Bitmaps, SkiaSharp surfaces, PNG byte arrays, and data URIs.",
            ReadingTime = "3 min read",
            Summary = "Display.Image renders graphics directly into the output deck with automatic format detection.",
            Keywords = new List<string> { "image", "bitmap", "skiasharp", "png", "jpeg", "graphics" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Supported Image Types",
                    Content = "Display.Image handles multiple graphic representations seamlessly:",
                    BulletPoints = new List<string>
                    {
                        "byte[] containing PNG, JPEG, GIF, or WebP bytes.",
                        "Avalonia.Media.Imaging.Bitmap instances.",
                        "SkiaSharp SKBitmap, SKImage, SKSurface, and SKData objects.",
                        "Base64 data URIs (data:image/png;base64,...).",
                        "File system paths (path/to/chart.png)."
                    }
                }
            }
        };
    }

    private DocArticle CreateAnimateAndCancellationArticle()
    {
        return new DocArticle
        {
            Id = "animate_and_cancellation",
            Title = "Live Animations & Cancellation",
            Subtitle = "60 FPS canvas drawing, live controls, animated charts, and cooperative cancellation.",
            ReadingTime = "6 min read",
            Summary = "Display.Animate draws a canvas on every frame without holding the kernel: the cell finishes and the animation carries on. Display.Control shows any Avalonia control, built on the UI thread. A chart handle's Update animates a chart. Display.ThrowIfCancellationRequested ensures loops stop cleanly.",
            Keywords = new List<string> { "animate", "animation", "fps", "drawingcontext", "cancellation", "token", "loop", "spinner", "particles", "oscilloscope", "progress", "control", "live" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Live 60 FPS Drawing (Display.Animate)",
                    Content = "Display.Animate((drawingContext, elapsed) => { ... }) invokes your callback on every frame. The script finishes immediately while the canvas keeps animating on its own. elapsed counts only the time the animation is on screen, so it carries on where it left off when its page comes back.",
                    CalloutType = DocCalloutType.Tip,
                    CalloutText = "Prefer Display.Animate over a while(true) loop — it doesn't hold any thread locks or freeze the host."
                },
                new()
                {
                    Heading = "Keep state outside the callback",
                    Content = "The callback runs every frame, so make brushes, pens and lists once above it and only change them inside it (the particle example below does). Use the difference between two elapsed values as the time step when something moves at a speed.",
                },
                new()
                {
                    Heading = "Brushes, pens and geometry made in the script",
                    Content = "A brush, pen or geometry made in your script belongs to the script's thread, and the frame callback draws on the UI thread. Use Brushes.X, new ImmutableSolidColorBrush(color) and new ImmutablePen(brush, width) (they can be made anywhere), make the others inside the callback, or make them in the setup step of Display.Animate(setup, frame), which runs once on the UI thread and hands what it made to every frame.",
                    CalloutType = DocCalloutType.Warning,
                    CalloutText = "If a frame draws with something from the wrong thread, the animation stops and its canvas says so (\"a different thread owns it\")."
                },
                new()
                {
                    Heading = "Live controls: build them with Display.Control",
                    Content = "An Avalonia control belongs to the thread that makes it, and only the UI thread can show one, while your script runs on another. Display.Control(() => new ProgressBar { ... }) builds the control on the UI thread for you; Display.Animate does the same. A control made in the script itself is refused with a message that says so.",
                    CalloutType = DocCalloutType.Warning,
                    CalloutText = "Put everything that sets a control up inside the Display.Control(() => ...) lambda, and return the control from it."
                },
                new()
                {
                    Heading = "Stopping, and when a frame goes wrong",
                    Content = "Stop() on the value Display.Animate returns freezes the animation on its last frame; Dispose() ends it and clears the canvas. Re-running a cell, clearing its output or closing its tab stops it too. If your frame callback throws, the animation stops and its canvas shows the exception, instead of staying blank."
                },
                new()
                {
                    Heading = "Animated charts and visualizers",
                    Content = "A chart is animated by changing it: keep the handle Display.LineChart(...) returns and call handle.Update(...) in a loop with a short await between steps (see the race example, and the Charts quick start). Algorithm visualizers animate by themselves: their play bar steps through what a VisualizerRecorder recorded."
                },
                new()
                {
                    Heading = "Cooperative Cancellation",
                    Content = "For finite rendering loops or long batch algorithms, call Display.ThrowIfCancellationRequested() inside your loop so the Stop button (Shift+F5) and timeout can interrupt execution."
                }
            },
            CodeSnippets = new List<DocCodeSnippet>
            {
                new()
                {
                    Id = "snip_live_animation",
                    Title = "Live Rotating Pulsing Radar",
                    Description = "Smooth 60 FPS immediate-mode animation using Display.Animate.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        Display.Animate((ctx, elapsed) =>
                        {
                            var center = new Avalonia.Point(200, 150);
                            double seconds = elapsed.TotalSeconds;
                            double radius = 60 + Math.Sin(seconds * 3) * 15;

                            // Draw pulsating ring
                            var pen = new Avalonia.Media.Pen(Avalonia.Media.Brushes.DeepSkyBlue, 2.5);
                            ctx.DrawEllipse(null, pen, center, radius, radius);

                            // Draw sweep line
                            double angle = seconds * 2;
                            var target = center + new Avalonia.Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
                            ctx.DrawLine(new Avalonia.Media.Pen(Avalonia.Media.Brushes.Aquamarine, 2), center, target);
                        }, width: 400, height: 300);
                        """
                },
                new()
                {
                    Id = "snip_anim_bouncing_ball",
                    Title = "Bouncing ball",
                    Description = "Position as a function of elapsed time: sine for the sideways drift, |sine| for the bounce.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        using Avalonia;
                        using Avalonia.Media;

                        const double width = 400, height = 220, radius = 16;
                        // Immutable brushes can be drawn with from the UI thread, wherever the script made them.
                        var ball = new ImmutableSolidColorBrush(Color.Parse("#F472B6"));
                        var room = new ImmutableSolidColorBrush(Color.Parse("#151B2B"));

                        Display.Animate((ctx, elapsed) =>
                        {
                            ctx.FillRectangle(room, new Rect(0, 0, width, height));

                            var t = elapsed.TotalSeconds;
                            var x = radius + (width - 2 * radius) * (0.5 + 0.5 * Math.Sin(t * 1.7));
                            var y = height - radius - (height - 2 * radius) * Math.Abs(Math.Sin(t * 2.3));
                            ctx.DrawEllipse(ball, null, new Point(x, y), radius, radius);
                        }, width: width, height: height);
                        """
                },
                new()
                {
                    Id = "snip_anim_spinner",
                    Title = "Loading spinner",
                    Description = "Dots chasing each other round a circle, fading as they trail.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        using Avalonia;
                        using Avalonia.Media;

                        const int dots = 12;
                        var colour = Brushes.DeepSkyBlue;

                        Display.Animate((ctx, elapsed) =>
                        {
                            var centre = new Point(100, 100);
                            var turn = elapsed.TotalSeconds * 2 * Math.PI; // one turn a second
                            for (var i = 0; i < dots; i++)
                            {
                                var angle = turn - i * 2 * Math.PI / dots;
                                var fade = 1 - i / (double)dots;
                                var at = centre + new Vector(Math.Cos(angle), Math.Sin(angle)) * 60;
                                using (ctx.PushOpacity(fade))
                                {
                                    ctx.DrawEllipse(colour, null, at, 3 + 7 * fade, 3 + 7 * fade);
                                }
                            }
                        }, width: 200, height: 200);
                        """
                },
                new()
                {
                    Id = "snip_anim_scope",
                    Title = "Oscilloscope",
                    Description = "A trace redrawn every frame from two moving sine waves.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        using Avalonia;
                        using Avalonia.Media;

                        const double width = 420, height = 200;
                        var grid = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1);
                        var trace = new ImmutablePen(new ImmutableSolidColorBrush(Colors.LimeGreen), 2);

                        Display.Animate((ctx, elapsed) =>
                        {
                            for (var y = 0.0; y <= height; y += 40) ctx.DrawLine(grid, new Point(0, y), new Point(width, y));

                            var t = elapsed.TotalSeconds;
                            var wave = new StreamGeometry();
                            using (var g = wave.Open())
                            {
                                for (var x = 0.0; x <= width; x += 3)
                                {
                                    var y = height / 2
                                        + Math.Sin(x / 30 + t * 4) * 50 * Math.Sin(t)
                                        + Math.Sin(x / 11 - t * 7) * 15;
                                    if (x == 0) g.BeginFigure(new Point(x, y), false);
                                    else g.LineTo(new Point(x, y));
                                }

                                g.EndFigure(false);
                            }

                            ctx.DrawGeometry(null, trace, wave);
                        }, width: width, height: height);
                        """
                },
                new()
                {
                    Id = "snip_anim_particles",
                    Title = "Falling particles (state and time step)",
                    Description = "Particles made once, moved each frame by speed × the time since the last frame.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        using Avalonia;
                        using Avalonia.Media;

                        const double width = 420, height = 240;
                        const int count = 70;
                        var random = new Random(3);
                        var x = new double[count];
                        var y = new double[count];
                        var speed = new double[count];
                        var size = new double[count];
                        for (var i = 0; i < count; i++)
                        {
                            x[i] = random.NextDouble() * width;
                            y[i] = random.NextDouble() * height;
                            speed[i] = 20 + random.NextDouble() * 70;
                            size[i] = 1 + random.NextDouble() * 3;
                        }

                        var snow = Brushes.WhiteSmoke;
                        var last = TimeSpan.Zero;

                        Display.Animate((ctx, elapsed) =>
                        {
                            var dt = (elapsed - last).TotalSeconds;
                            last = elapsed;
                            for (var i = 0; i < count; i++)
                            {
                                y[i] = (y[i] + speed[i] * dt) % height;
                                x[i] += Math.Sin(elapsed.TotalSeconds + i) * 8 * dt;
                                ctx.DrawEllipse(snow, null, new Point(x[i], y[i]), size[i], size[i]);
                            }
                        }, width: width, height: height);
                        """
                },
                new()
                {
                    Id = "snip_anim_clock",
                    Title = "A clock (setup step for pens and brushes)",
                    Description = "Display.Animate(setup, frame): the setup step runs once on the UI thread, so it can make any brush, pen or geometry.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        using Avalonia;
                        using Avalonia.Media;

                        Display.Animate(
                            setup: () => (
                                Face: new Pen(Brushes.SlateGray, 4),
                                Hour: new Pen(Brushes.White, 5, lineCap: PenLineCap.Round),
                                Minute: new Pen(Brushes.LightSkyBlue, 3, lineCap: PenLineCap.Round),
                                Second: new Pen(Brushes.Tomato, 1.5)),
                            onFrame: (ctx, elapsed, pens) =>
                            {
                                var centre = new Point(100, 100);
                                var now = DateTime.Now;
                                ctx.DrawEllipse(null, pens.Face, centre, 90, 90);

                                Point Tip(double turns, double length) =>
                                    centre + new Vector(Math.Sin(turns * 2 * Math.PI), -Math.Cos(turns * 2 * Math.PI)) * length;

                                ctx.DrawLine(pens.Hour, centre, Tip((now.Hour % 12 + now.Minute / 60.0) / 12, 45));
                                ctx.DrawLine(pens.Minute, centre, Tip((now.Minute + now.Second / 60.0) / 60, 65));
                                ctx.DrawLine(pens.Second, centre, Tip((now.Second + now.Millisecond / 1000.0) / 60, 78));
                            },
                            width: 200, height: 200);
                        """
                },
                new()
                {
                    Id = "snip_anim_stop",
                    Title = "Stop an animation from the script",
                    Description = "The value Display.Animate returns can be stopped: here after three seconds. Stop() keeps the last frame; Dispose() clears the canvas.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        using Avalonia;
                        using Avalonia.Media;

                        var pulse = Display.Animate((ctx, elapsed) =>
                        {
                            var r = 25 + 15 * Math.Sin(elapsed.TotalSeconds * 4);
                            ctx.DrawEllipse(Brushes.Orange, null, new Point(60, 60), r, r);
                        }, width: 120, height: 120);

                        await Task.Delay(3000, Display.CancellationToken);
                        pulse.Stop();
                        Console.WriteLine("Stopped: the timer is off, and the last frame stays on screen.");
                        """
                },
                new()
                {
                    Id = "snip_anim_controls",
                    Title = "Live controls (built on the UI thread)",
                    Description = "Display.Control builds the control for you; an indeterminate progress bar animates by itself.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        Display.Control(() => new Avalonia.Controls.ProgressBar { IsIndeterminate = true, Width = 320 });

                        Display.Control(() => new Avalonia.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 65, Width = 320 });
                        """
                },
                new()
                {
                    Id = "snip_anim_bar_race",
                    Title = "Animated bar race (a chart changing over time)",
                    Description = "Update the chart's handle in a loop; Display.ThrowIfCancellationRequested lets Stop end it.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        var names = new[] { "Ada", "Bo", "Cy", "Di", "Eli" };
                        var scores = new double[] { 5, 3, 8, 2, 6 };
                        var random = new Random(1);

                        var race = Display.BarChart(
                            names.Zip(scores, (name, score) => (name, score)).ToDictionary(p => p.name, p => p.score),
                            "Race", yLabel: "points");

                        for (var step = 0; step < 25; step++)
                        {
                            Display.ThrowIfCancellationRequested();
                            await Task.Delay(120, Display.CancellationToken);

                            for (var i = 0; i < scores.Length; i++) scores[i] += random.NextDouble() * 3;
                            race.Update(s => s.Series[0].Y = scores.Select(v => (double?)v).ToList());
                        }
                        """
                }
            }
        };
    }

    private DocArticle CreateMultiLanguageDisplayArticle()
    {
        return new DocArticle
        {
            Id = "multilang_display_api",
            Title = "Visual Displays in Java, Python, and JavaScript",
            Subtitle = "Use Display.table(), Display.show(), Display.html(), and Display.image() across all languages.",
            ReadingTime = "3 min read",
            Summary = "FrySharp provides zero-configuration visual output for Java, Python, and JavaScript. Call Display.dump() or dump() to inspect arrays, collections, matrices, dataframes, and objects in rich interactive tables.",
            Keywords = new List<string> { "java", "python", "javascript", "display", "polyglot", "results", "table", "html", "image" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Polyglot Visual Displays",
                    Content = "Just like C#'s .Dump() method, Java, Python, and JavaScript scripts running in FrySharp can emit interactive tables, formatted HTML, and images directly into the Results (.Dump) dock tab.",
                    CalloutType = DocCalloutType.Tip,
                    CalloutText = "In Java and Python, Display and dump() are automatically available with zero imports required in your scripts!"
                }
            },
            ApiSignatures = new List<DocApiSignature>
            {
                new()
                {
                    MethodName = "Display.table(obj)",
                    ReturnType = "Object",
                    Parameters = "Object obj, String? title = null",
                    Description = "Emits an interactive visual table for any array, collection, matrix, or object into the Results tab (Java, JS, Python)."
                },
                new()
                {
                    MethodName = "Display.show(obj, title)",
                    ReturnType = "Object",
                    Parameters = "obj, title = None",
                    Description = "Emits an interactive visual inspection of any data structure into the Results tab."
                },
                new()
                {
                    MethodName = "Display.html(htmlContent)",
                    ReturnType = "void",
                    Parameters = "String htmlContent",
                    Description = "Renders rich interactive HTML directly in the Results bottom dock tab."
                },
                new()
                {
                    MethodName = "Display.image(bytesOrPath)",
                    ReturnType = "void",
                    Parameters = "byte[] / String bytesOrPath",
                    Description = "Renders PNG/JPEG image data or matplotlib figures directly in the Results bottom dock tab."
                }
            },
            CodeSnippets = new List<DocCodeSnippet>
            {
                new()
                {
                    Id = "snip_java_display",
                    Title = "Java: Array & Object Inspection",
                    Description = "Call Display.dump() on arrays, collections, or records without any imports.",
                    Language = "java",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        int[] data = { 64, 34, 25, 12, 22, 11, 90, 88, 45, 50, 7 };
                        Display.dump("Original Array", data);

                        Arrays.sort(data);
                        Display.dump("Sorted Array", data);
                        """
                },
                new()
                {
                    Id = "snip_python_display",
                    Title = "Python: Global display() & DataFrames",
                    Description = "Inspect lists, dictionaries, pandas DataFrames, and matplotlib plots.",
                    Language = "python",
                    TargetKind = WorkspaceItemKind.Script,
                    NotRunnable = true, // requires external package: pandas
                    Code = """
                        data = [64, 34, 25, 12, 22, 11, 90, 88, 45, 50, 7]
                        data.sort()
                        dump(data, "Sorted Numbers")

                        # Or using pandas
                        import pandas as pd
                        df = pd.DataFrame({"Name": ["Alice", "Bob"], "Score": [98, 85]})
                        dump(df)
                        """
                },
                new()
                {
                    Id = "snip_js_display",
                    Title = "JavaScript: Global display() & Objects",
                    Description = "Inspect arrays and objects in Node.js scripts.",
                    Language = "javascript",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = """
                        const data = [64, 34, 25, 12, 22, 11, 90, 88, 45, 50, 7];
                        data.sort((a, b) => a - b);
                        Display.dump(data, "Sorted Data");
                        """
                }
            }
        };
    }

    private DocArticle CreateShortcutsReferenceArticle()
    {
        return new DocArticle
        {
            Id = "shortcuts_reference",
            Title = "VS Code Shortcuts Cheat Sheet",
            Subtitle = "Complete cross-platform keybindings reference for FrySharp.",
            ReadingTime = "2 min read",
            Summary = "Standard Visual Studio Code keyboard shortcuts across macOS and Windows/Linux.",
            Keywords = new List<string> { "shortcuts", "keybindings", "f5", "f10", "cmd+b", "ctrl+b", "ctrl+j", "mac", "windows" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Authentic VS Code Ergonomics",
                    Content = "All major shortcuts are wired into the studio view and can be triggered anywhere in the active window.",
                    CalloutType = DocCalloutType.Info,
                    CalloutText = "On macOS, the Command key (⌘) is automatically honored wherever Ctrl is used on Windows."
                }
            },
            Shortcuts = new List<DocShortcutItem>
            {
                new() { Action = "Toggle Primary Side Bar", MacKey = "⌘ B", WinKey = "Ctrl+B", Description = "Hide or show Explorer, Search, or Debug sidebar.", Category = "Window" },
                new() { Action = "Toggle Bottom Tool Deck", MacKey = "⌘ J", WinKey = "Ctrl+J", Description = "Hide or show Problems, Terminal, REPL, and Results.", Category = "Window" },
                new() { Action = "Save Document", MacKey = "⌘ S", WinKey = "Ctrl+S", Description = "Save active script or notebook to disk.", Category = "File" },
                new() { Action = "Start Debugging / Continue", MacKey = "F5", WinKey = "F5", Description = "Compile, launch debugger, or continue past breakpoint.", Category = "Debug" },
                new() { Action = "Run without Debugging", MacKey = "Ctrl+F5", WinKey = "Ctrl+F5", Description = "Fast execution without debugger hooks.", Category = "Debug" },
                new() { Action = "Stop Execution / Debug", MacKey = "⇧ F5", WinKey = "Shift+F5", Description = "Cancel running script or terminate debug session.", Category = "Debug" },
                new() { Action = "Step Over", MacKey = "F10", WinKey = "F10", Description = "Execute next statement without stepping into methods.", Category = "Debug" },
                new() { Action = "Step Into", MacKey = "F11", WinKey = "F11", Description = "Step into method under cursor.", Category = "Debug" },
                new() { Action = "Toggle Breakpoint", MacKey = "F9", WinKey = "F9", Description = "Toggle breakpoint on current cursor line.", Category = "Debug" },
                new() { Action = "Format Document", MacKey = "⇧ ⌥ F", WinKey = "Ctrl+K Ctrl+D", Description = "Format C# code using Roslyn formatter.", Category = "Editor" },
                new() { Action = "Show Hover (Quick Info)", MacKey = "⌘ K ⌘ I", WinKey = "Ctrl+K Ctrl+I", Description = "Show the signature and documentation of the symbol at the cursor; resting the mouse on a symbol shows the same card.", Category = "Editor" },
                new() { Action = "Find & Replace", MacKey = "⌘ F", WinKey = "Ctrl+F", Description = "Open editor find and replace overlay.", Category = "Editor" },
                new() { Action = "Run Notebook Cell", MacKey = "⇧ Enter", WinKey = "Shift+Enter", Description = "Execute active cell and advance to next cell.", Category = "Notebook" },
                new() { Action = "Focus Explorer", MacKey = "⇧ ⌘ E", WinKey = "Ctrl+Shift+E", Description = "Open SideBar and focus workspace Explorer.", Category = "Navigation" },
                new() { Action = "Focus Search", MacKey = "⇧ ⌘ F", WinKey = "Ctrl+Shift+F", Description = "Open SideBar and focus workspace text Search.", Category = "Navigation" },
                new() { Action = "Focus Run & Debug", MacKey = "⇧ ⌘ D", WinKey = "Ctrl+Shift+D", Description = "Open SideBar and focus Run & Debug view.", Category = "Navigation" },
                new() { Action = "Focus Problems", MacKey = "⇧ ⌘ M", WinKey = "Ctrl+Shift+M", Description = "Open Bottom Deck and focus Problems tab.", Category = "Navigation" }
            }
        };
    }
}
