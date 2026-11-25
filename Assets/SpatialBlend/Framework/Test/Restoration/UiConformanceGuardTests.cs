// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The guard of the B Graphite design system: nothing a user can see in the Freeform app or the desktop
    /// spectator may use a typeface, a colour or a toolkit outside <see cref="UiTheme"/> and <see cref="UiFonts"/>, unless it is in the
    /// allow-lists below with its reason. A new surface that forgets the theme fails here, not in a headset.
    ///
    /// Scans: (1) every prefab under Assets/SpatialBlend and the two shipped scenes, for TMP text (font) and uGUI graphics (colour),
    /// and for legacy uGUI Text; (2) the runtime C# of the UI folders, for IMGUI, named or literal colours, and non-UI fonts.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class UiConformanceGuardTests
    {
        private static readonly string[] Scenes =
        {
            "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity",
            "Assets/SpatialBlend/Samples/Scenes/FreeformSpectator.unity",
        };

        /// <summary>Prefabs whose TMP text is allowed to keep the TMP default font, and why.</summary>
        private static readonly Dictionary<string, string> FontAllowList = new Dictionary<string, string>
        {
            {
                "Assets/SpatialBlend/Samples/Prefabs/UI/ControlPanel.prefab",
                "The legacy rows of the pre-redesign layout. ControlPanel.BuildGeneratedLayout hides every one of them and builds the " +
                "generated panel (Pretendard) instead; they stay only so the scene's serialized references keep resolving."
            },
        };

        /// <summary>
        /// Runtime C# files allowed to hold a literal colour, and why. A path is relative to Assets/SpatialBlend/. Everything else in the UI
        /// folders takes its colours from <see cref="UiTheme"/>.
        /// </summary>
        private static readonly Dictionary<string, string> ColourAllowList = new Dictionary<string, string>
        {
            { "Samples/Scripts/UI/NavMeshVisualizer.cs", "Debug mesh of the navigation toggle that the generated panel no longer offers; no scene or prefab instance carries it." },
            { "Samples/Scripts/UI/OutlineGlow.cs", "Legacy v1 outline; its only user is the Local Simulator (Test scene, not in the build). The scan preview has no outline: its box carries a floor ring in the accent (GltfLoader.BuildFootprintRing)." },
            { "Samples/Scanning/Scripts/PassthroughPanelManager.cs", "Debug dots drawn on the developer-only debug photo dump (blue, red, green marks of picked points) and the transparent pixel of the mask texture: pixels of an image, not a UI surface; every colour the panel shows comes from UiTheme." },
            { "Samples/Scripts/UI/Theme/UiTheme.cs", "The theme itself: it defines the tokens (hex strings parsed once into the palette). Every other surface takes its colours from it." },
            { "Samples/Scripts/Tutorial/TutorialPractice.cs", "The tint of a 3D practice prop (a vase body), not a UI surface." },
            { "Samples/Scripts/UI/Scan/ScanCard.cs", "The photo's reticle and point dots: an opaque white ring and saturated green (positive) / red (negative) dots drawn over the captured photo, an image overlay, not UI; every other colour of the card comes from UiTheme." },
            { "Samples/Scripts/Tutorial/TutorialHologramCapture.cs", "The clear colour and the vase ink of the practice photo it renders (pixels of an image the canned server segments), not a UI surface." },
        };

        /// <summary>Runtime C# files allowed IMGUI, and why. A path is relative to Assets/SpatialBlend/.</summary>
        private static readonly Dictionary<string, string> ImguiAllowList = new Dictionary<string, string>
        {
            { "Freeform/Desktop/FreeformLocalApp.cs", "A developer demo component (docs/freeform-local-desktop.md) added by hand to any scene; no shipped scene or prefab carries it." },
        };

        /// <summary>Folders whose runtime C# draws UI (the scan panel is Samples/Scanning): scanned for IMGUI, literal colours and non-UI fonts.</summary>
        private static readonly string[] UiFolders =
        {
            "Samples/Scripts/UI", "Samples/Scripts/Spectator", "Samples/Scripts/Observer", "Samples/Scripts/Tutorial", "Interaction/Scripts/UI", "Samples/Scanning/Scripts",
        };

        private static readonly Regex NamedColour = new Regex(@"\bColor\s*\.\s*(red|green|blue|yellow|cyan|magenta|gray|grey|black)\b");
        // A literal r, g, b of full white is a sprite mask (the alpha shape is baked, a token tints it), not a colour choice.
        // Both spellings: new Color(...) / new Color (...), and the target-typed new(...) of a Color-typed declaration (Color c = new(...), Color C => new(...)).
        private const string NotWhite = @"(?!(255|1(\.0*)?f?)\s*,\s*(255|1(\.0*)?f?)\s*,\s*(255|1(\.0*)?f?)\s*[,)])[0-9.]+f?\s*,";
        private static readonly Regex LiteralColour = new Regex(@"new\s+Color(32)?\s*\(\s*" + NotWhite);
        private static readonly Regex TargetTypedColour = new Regex(@"\bColor(32)?\s+\w+\s*(=|=>)\s*new\s*\(\s*" + NotWhite);
        // A colour made another way is still a colour choice outside the theme: parsed from a hex string, from HSV, or a rich-text tag in a string.
        private static readonly Regex ParsedColour = new Regex(@"\bColorUtility\s*\.\s*TryParseHtmlString\b|\bColor\s*\.\s*HSVToRGB\b");
        private static readonly Regex RichTextColour = new Regex(@"<\s*color\s*=\s*(\\?"")?#|<#[0-9A-Fa-f]{3,8}>", RegexOptions.IgnoreCase);
        private static bool HasLiteralColour(string code) =>
            LiteralColour.IsMatch(code) || TargetTypedColour.IsMatch(code) || ParsedColour.IsMatch(code) || RichTextColour.IsMatch(code);
        private static readonly Regex NonUiFont = new Regex(@"Arial|LiberationSans|GetBuiltinResource\s*<\s*Font|new\s+Font\(|CreateDynamicFontFromOSFont");
        private static readonly Regex Imgui = new Regex(@"\bOnGUI\s*\(|\bGUILayout\.|\bGUI\.(Label|Button|Box|TextField)");

        public static string[] AllPrefabs => AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/SpatialBlend" })
            .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p).ToArray();

        private static bool IsUiFont(TMP_FontAsset font) =>
            font != null && UiFonts.Set != null && (font == UiFonts.Regular || font == UiFonts.SemiBold);

        private static bool Near(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 2e-3f && Mathf.Abs(a.g - b.g) < 2e-3f && Mathf.Abs(a.b - b.b) < 2e-3f;

        private static bool IsToken(Color c)
        {
            if (c.a < 1e-3f) return true;   // invisible host
            if (Near(c, Color.white)) return true;   // a sprite or gradient tinted by its own token
            var p = UiTheme.Palette;
            foreach (var token in new[]
            {
                p.PanelTop, p.PanelBottom, p.Surface, p.SurfaceHover, p.SurfacePressed, p.SurfaceDisabled, p.Outline, p.Accent, p.AccentHover,
                p.AccentPressed, p.OnAccent, p.Text, p.TextMuted, p.TextDisabled, p.Track, p.Knob, p.Success, p.Warning, p.Error, p.RealAnchor, p.SharedReal, p.Shadow,
            })
                if (Near(c, token)) return true;
            return false;
        }

        private static IEnumerable<string> Describe(IEnumerable<Component> components, string where, bool allowDefaultFont)
        {
            foreach (var component in components)
            {
                if (component is TMP_Text text && !allowDefaultFont && !IsUiFont(text.font))
                    yield return $"{where}: TMP text '{Path(component)}' uses font '{(text.font != null ? text.font.name : "none")}', not the Pretendard UI font set";
                if (component is Text legacy)
                    yield return $"{where}: legacy uGUI Text '{Path(component)}' (font '{(legacy.font != null ? legacy.font.name : "none")}'); use TextMeshProUGUI with UiFonts";
            }
        }

        private static string Path(Component c)
        {
            var parts = new List<string>();
            for (var t = c.transform; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        [Test]
        public void TheUiFontSetIsPretendard()
        {
            Assert.That(UiFonts.Set, Is.Not.Null);
            Assert.That(UiFonts.Regular.name, Does.StartWith("Pretendard"));
            Assert.That(UiFonts.SemiBold.name, Does.StartWith("Pretendard"));
        }

        [Test]
        public void EveryPrefabsTextUsesThePretendardFontSetAndNoLegacyText()
        {
            var findings = new List<string>();
            foreach (string path in AllPrefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                bool allow = FontAllowList.ContainsKey(path);
                findings.AddRange(Describe(prefab.GetComponentsInChildren<Component>(true).Where(c => c != null), path, allow));
            }
            Assert.That(findings, Is.Empty, string.Join("\n", findings));
        }

        [Test]
        public void EveryPrefabsGraphicsUseThemeColours()
        {
            var findings = new List<string>();
            foreach (string path in AllPrefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                // Only prefabs that carry a Canvas are UI; the allow-listed prefab's legacy rows are hidden at runtime (see FontAllowList).
                if (prefab.GetComponentInChildren<Canvas>(true) == null || FontAllowList.ContainsKey(path)) continue;
                foreach (var graphic in prefab.GetComponentsInChildren<Graphic>(true))
                    if (!IsToken(graphic.color))
                        findings.Add($"{path}: '{Path(graphic)}' colour {(Color32)graphic.color} is not a UiTheme token");
            }
            Assert.That(findings, Is.Empty, string.Join("\n", findings));
        }

        [Test]
        public void TheShippedScenesCarryNoOffThemeText([ValueSource(nameof(Scenes))] string scenePath)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                var findings = new List<string>();
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var component in root.GetComponentsInChildren<Component>(true).Where(c => c != null))
                    {
                        // A component that comes from an allow-listed prefab keeps that prefab's allowance.
                        var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(component.gameObject);
                        string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
                        bool allow = sourcePath != null && FontAllowList.ContainsKey(sourcePath);
                        // Packages' own prefabs (Meta SDK, Photon) are not the app's UI; their instances are listed by prefab path.
                        bool foreign = sourcePath != null && !sourcePath.StartsWith("Assets/SpatialBlend/");
                        if (foreign) continue;
                        findings.AddRange(Describe(new[] { component }, scenePath, allow));
                        // A uGUI graphic made in the scene itself (not an instance of a prefab) takes its colour from the theme as well. An
                        // instance of an app prefab is checked through its prefab; a scene's own override of it is not a scene-native graphic.
                        if (component is Graphic graphic && source == null && component.GetComponentInParent<Canvas>(true) != null && !IsToken(graphic.color))
                            findings.Add($"{scenePath}: scene-native '{Path(component)}' colour {(Color32)graphic.color} is not a UiTheme token");
                    }
                Assert.That(findings, Is.Empty, string.Join("\n", findings));
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static IEnumerable<(string rel, string text)> UiSources()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "SpatialBlend"));
            foreach (string folder in UiFolders)
            {
                string dir = System.IO.Path.Combine(root, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(root.Length + 1).Replace('\\', '/');
                    if (rel.Contains("/Editor/")) continue;
                    yield return (rel, File.ReadAllText(file));
                }
            }
        }

        private static string StripComments(string source) =>
            Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//.*", string.Empty);

        [Test]
        public void NoUiSourceUsesImguiANonUiFontOrALiteralColour()
        {
            var findings = new List<string>();
            foreach (var (rel, raw) in UiSources())
            {
                string code = StripComments(raw);
                if (Imgui.IsMatch(code)) findings.Add($"{rel}: IMGUI (OnGUI/GUILayout) draws the Unity default skin; build the surface with ControlPanelKit");
                if (NonUiFont.IsMatch(code)) findings.Add($"{rel}: names a font other than the UI font set");
                if (ColourAllowList.ContainsKey(rel)) continue;
                if (NamedColour.IsMatch(code)) findings.Add($"{rel}: a named Color (Color.red ...) outside UiTheme; use UiTheme.Palette");
                if (HasLiteralColour(code)) findings.Add($"{rel}: a literal colour (new Color(...), new(...), a parsed hex, HSVToRGB or a <color=#...> tag) outside UiTheme; derive it from UiTheme.Palette");
            }
            Assert.That(findings, Is.Empty, string.Join("\n", findings));
        }

        [Test]
        public void EveryAllowListEntryStillExistsAndStillNeedsItsAllowance()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "SpatialBlend"));
            foreach (var pair in ColourAllowList)
            {
                string file = System.IO.Path.Combine(root, pair.Key);
                Assert.That(File.Exists(file), Is.True, pair.Key + " is allow-listed but gone: remove the entry");
                string code = StripComments(File.ReadAllText(file));
                Assert.That(NamedColour.IsMatch(code) || HasLiteralColour(code), Is.True, pair.Key + " no longer holds a literal colour: remove the entry");
            }
            foreach (var pair in FontAllowList)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(pair.Key);
                Assert.That(prefab, Is.Not.Null, pair.Key + " is allow-listed but gone: remove the entry");
                // Still needed: with no allowance the prefab would be reported. Once it conforms, the entry only hides a future regression.
                Assert.That(Describe(prefab.GetComponentsInChildren<Component>(true).Where(c => c != null), pair.Key, false), Is.Not.Empty,
                    pair.Key + " now uses the UI font everywhere: remove the entry");
            }
            foreach (var pair in ImguiAllowList)
            {
                string file = System.IO.Path.Combine(root, pair.Key);
                Assert.That(File.Exists(file), Is.True, pair.Key + " is allow-listed but gone: remove the entry");
                Assert.That(Imgui.IsMatch(StripComments(File.ReadAllText(file))), Is.True, pair.Key + " no longer uses IMGUI: remove the entry");
            }
        }

        // The guard's own patterns are checked against a planted positive for every form the audit listed, and against the white sprite mask
        // they must let through: a regex that matches nothing passes every file.
        [TestCase("var c = new Color(0.2f, 0.3f, 0.4f, 1f);")]
        [TestCase("var c = new Color (0.2f, 0.3f, 0.4f);")]
        [TestCase("var c = new Color32(20, 30, 40, 255);")]
        [TestCase("Color c = new(0.2f, 0.3f, 0.4f, 1f);")]
        [TestCase("static Color Edge => new(0.2f, 0.3f, 0.4f);")]
        [TestCase("Color32 c = new (20, 30, 40, 255);")]
        [TestCase("ColorUtility.TryParseHtmlString(\"#23262E\", out var c);")]
        [TestCase("ColorUtility . TryParseHtmlString(hex, out c);")]
        [TestCase("var c = Color.HSVToRGB(0.5f, 0.5f, 0.5f);")]
        [TestCase("label.text = \"<color=#FF8800>warm</color>\";")]
        [TestCase("label.text = \"<color=\\\"#FF8800\\\">warm</color>\";")]
        [TestCase("label.text = \"<#FF8800>warm\";")]
        public void TheLiteralColourPatternsCatchEveryFormTheAuditListed(string code)
        {
            Assert.That(HasLiteralColour(code), Is.True, code);
        }

        [TestCase("var mask = new Color(1f, 1f, 1f, 0.5f);")]
        [TestCase("var mask = new Color32(255, 255, 255, 40);")]
        [TestCase("Color mask = new(1f, 1f, 1f, 0.5f);")]
        [TestCase("var c = new Vector3(0.2f, 0.3f, 0.4f);")]
        [TestCase("Vector3 p = new(0.2f, 0.3f, 0.4f);")]
        [TestCase("var tint = UiTheme.Palette.Accent;")]
        [TestCase("label.text = \"Press <b>A</b> to continue\";")]
        public void TheLiteralColourPatternsLetAWhiteMaskAndNonColoursThrough(string code)
        {
            Assert.That(HasLiteralColour(code), Is.False, code);
        }

        [TestCase("var c = Color.red;")]
        [TestCase("var c = Color . black;")]
        public void TheNamedColourPatternCatchesSpacedForms(string code)
        {
            Assert.That(NamedColour.IsMatch(code), Is.True, code);
        }

        [Test]
        public void TheAppsOwnCodeOutsideTheUiFoldersHasNoImguiEitherExceptTheLocalSimulator()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "SpatialBlend"));
            var findings = new List<string>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = file.Substring(root.Length + 1).Replace('\\', '/');
                if (rel.Contains("/Editor/") || rel.StartsWith("Editor/") || rel.Contains("/Test/") || rel.StartsWith("Framework/Test/")) continue;
                // The Local Simulator is the editor-only Test scene (disabled in the build settings): its OnGUI is a developer overlay.
                if (rel.StartsWith("Samples/Scripts/Local Simulator/") || ImguiAllowList.ContainsKey(rel)) continue;
                if (Imgui.IsMatch(StripComments(File.ReadAllText(file)))) findings.Add(rel);
            }
            Assert.That(findings, Is.Empty, "IMGUI in shipped code: " + string.Join(", ", findings));
        }
    }
}
#endif
