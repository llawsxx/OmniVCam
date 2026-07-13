using System;
using System.IO;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    internal static class ControllerLanguage
    {
        private const string AutoConfigFileName = "OmniVCamController.xml";
        private const string LanguageAttributeName = "uiCulture";

        public static string LoadSavedCultureName()
        {
            try
            {
                string path = GetAutoConfigPath();
                if (!File.Exists(path)) return string.Empty;
                XDocument document = XDocument.Load(path);
                XAttribute attribute = document.Root?.Attribute(LanguageAttributeName);
                return attribute == null ? string.Empty : attribute.Value;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void SetLanguageAttribute(XElement root, string cultureName)
        {
            if (root == null) return;
            if (string.IsNullOrWhiteSpace(cultureName)) root.Attribute(LanguageAttributeName)?.Remove();
            else root.SetAttributeValue(LanguageAttributeName, cultureName);
        }

        private static string GetAutoConfigPath()
        {
            return Path.Combine(Application.StartupPath, AutoConfigFileName);
        }
    }
}
