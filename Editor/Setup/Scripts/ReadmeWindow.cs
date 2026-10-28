using UnityEditor;

using UnityEngine;
using UnityEngine.UIElements;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// Short orientation for a creator whose project is set up: what the SDK is, the first steps
    /// and where its windows are. It opens when the project opens and nothing needs setup (see
    /// <see cref="SetupWindowStartup"/>), and again from <b>Virtuademy &gt; Readme</b>.
    ///
    /// It is a window rather than an asset with an inspector, as Unity's URP template has: a package
    /// is read-only, so a Readme asset would have to be copied into every project to be
    /// selectable, and a copy goes stale. The text is in this file; the "do not show again"
    /// choice is in <see cref="SetupUserSettings"/>.
    /// </summary>
    public class ReadmeWindow : EditorWindow
    {
        private const string package_root = "Packages/com.anotherealitysrl.virtuademy-sdk-environments-setup";
        private const string style_sheet_path = package_root + "/Editor/Setup/Scripts/readme.uss";

        [MenuItem("Virtuademy/Readme")]
        public static void ShowWindow()
        {
            // The minimum size is set when the window is created, so a person's resize is not undone
            // every time the menu is used.
            bool created = !HasOpenInstances<ReadmeWindow>();
            ReadmeWindow window = GetWindow<ReadmeWindow>();
            window.titleContent = new GUIContent("Virtuademy Readme");
            if (created)
                window.minSize = new Vector2(420, 480);
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.AddToClassList("vd-readme");

            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(style_sheet_path);
            if (sheet != null)
                root.styleSheets.Add(sheet);

            ScrollView scroll = new() { name = "readme-scroll" };
            scroll.AddToClassList("vd-readme__scroll");
            root.Add(scroll);

            scroll.Add(BuildHeader());

            scroll.Add(Heading("What the SDK is"));
            scroll.Add(Paragraph(
                "The Virtuademy SDK is the toolkit for building your own environments in Unity and publishing them " +
                "to a Virtuademy platform. It comes as a set of packages, which the setup window (Virtuademy > Setup project) " +
                "installs and keeps for the platform version you choose."));

            scroll.Add(Heading("Your first steps"));
            scroll.Add(Step("1", "Set up the scene",
                "Open or create a scene. Add the Floor Placeholder script (Packages > Virtuademy-SDK-Environments > ScriptingApi > " +
                "Placeholders > Fundamentals) to the object that is your floor, so VR users can teleport on it, and a spawn point, the " +
                "SpawnPoint prefab (Packages > Virtuademy-SDK-Environments > Prefabs > Fundamentals), where avatars appear."));
            scroll.Add(Step("2", "Add placeholders and your own logic",
                "Place the SDK's prefabs and placeholders in the scene. For behaviour, use Visual Scripting with the Virtuademy " +
                "nodes, or write C# scripts: they run through the interpreter, which the setup window installs on request."));
            scroll.Add(Step("3", "Build and publish",
                "Open Virtuademy > Publish Environments, pick the scenes and platforms, press Build, then deploy to a world. " +
                "Deploying needs a connection: import the configuration file from the Back Office and log in (the Connection step of the setup window)."));
            scroll.Add(Step("4", "Test it",
                "Open the world you deployed to from the platform and check it, on each platform you built for."));

            scroll.Add(Heading("Menu entries"));
            scroll.Add(MenuRow("Virtuademy > Setup project", "The setup window: checks this computer and project, installs and updates the SDK."));
            scroll.Add(MenuRow("Virtuademy > Publish Environments", "Build your scenes and deploy them to your worlds. SDK versions before 2026.7 call it Addressables management."));
            scroll.Add(MenuRow("Virtuademy > Update routines", "One entry per platform version step. Run the one the release notes name after you move the project to a new version."));
            scroll.Add(MenuRow("Virtuademy > Update VS Library", "Adds the Virtuademy nodes to the Visual Scripting node library."));
            scroll.Add(MenuRow("Virtuademy > Readme", "This page."));
            scroll.Add(Paragraph("Which entries exist depends on the SDK version installed."));

            scroll.Add(Heading("Documentation"));
            VisualElement links = new();
            links.AddToClassList("vd-readme__links");
            links.Add(LinkButton("Documentation site", SdkWindowLinks.documentation_root_url));
            links.Add(LinkButton("SDK documentation", SdkWindowLinks.documentation_sdk_url));
            scroll.Add(links);

            root.Add(BuildFooter());
        }

        private static VisualElement BuildHeader()
        {
            VisualElement header = new();
            header.AddToClassList("vd-readme__header");

            VisualElement logo = new();
            logo.AddToClassList("vd-readme__logo");

            // Light lettering on the dark skin, dark lettering on the light one: the same choice as the setup window.
            string variant = EditorGUIUtility.isProSkin ? "light" : "dark";
            Texture2D image = AssetDatabase.LoadAssetAtPath<Texture2D>($"{package_root}/Editor/Setup/Icons/virtuademy-logo-{variant}.png");
            if (image != null)
                logo.style.backgroundImage = image;
            else
                logo.style.display = DisplayStyle.None;
            header.Add(logo);

            Label title = new("Welcome to the Virtuademy SDK");
            title.AddToClassList("vd-readme__title");
            header.Add(title);
            return header;
        }

        private VisualElement BuildFooter()
        {
            VisualElement footer = new();
            footer.AddToClassList("vd-readme__footer");

            Toggle hide = new("Do not show this when the project opens");
            hide.AddToClassList("vd-readme__hide-toggle");
            hide.tooltip = "Saved for this project and this user, in UserSettings/Virtuademy/setup.json. " +
                           "You can always open the Readme again from Virtuademy > Readme.";
            hide.SetValueWithoutNotify(SetupUserSettings.HideReadmeOnStartup);
            hide.RegisterValueChangedCallback(evt => SetupUserSettings.HideReadmeOnStartup = evt.newValue);
            footer.Add(hide);

            Button setup = new(CreatorKitSetupWindow.ShowWindow) { text = "Open setup window" };
            footer.Add(setup);
            return footer;
        }

        private static Label Heading(string text)
        {
            Label label = new(text);
            label.AddToClassList("vd-readme__heading");
            return label;
        }

        private static Label Paragraph(string text)
        {
            Label label = new(text);
            label.AddToClassList("vd-readme__text");
            return label;
        }

        private static VisualElement Step(string number, string title, string text)
        {
            VisualElement row = new();
            row.AddToClassList("vd-readme__step");

            Label badge = new(number);
            badge.AddToClassList("vd-readme__step-number");
            row.Add(badge);

            VisualElement body = new();
            body.AddToClassList("vd-readme__step-body");
            Label titleLabel = new(title);
            titleLabel.AddToClassList("vd-readme__step-title");
            body.Add(titleLabel);
            body.Add(Paragraph(text));
            row.Add(body);
            return row;
        }

        private static VisualElement MenuRow(string path, string text)
        {
            VisualElement row = new();
            row.AddToClassList("vd-readme__menu-row");

            Label pathLabel = new(path);
            pathLabel.AddToClassList("vd-readme__menu-path");
            row.Add(pathLabel);
            row.Add(Paragraph(text));
            return row;
        }

        private static Button LinkButton(string text, string url)
        {
            Button button = new(() => Application.OpenURL(url)) { text = text, tooltip = url };
            return button;
        }
    }
}
