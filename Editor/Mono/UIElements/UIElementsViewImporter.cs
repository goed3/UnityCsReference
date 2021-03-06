// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ExCSS;
using UnityEditor.Experimental.AssetImporters;
using UnityEditor.StyleSheets;
using UnityEngine;

using UnityEngine.UIElements;

using StyleSheet = UnityEngine.UIElements.StyleSheet;

namespace UnityEditor.UIElements
{
    // Make sure UXML is imported after assets than can be addressed in USS
    [ScriptedImporter(version: 6, ext: "uxml", importQueueOffset: 1000)]
    internal class UIElementsViewImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            VisualTreeAsset vta;

            var importer = new UXMLImporterImpl(args);

            importer.Import(out vta);
            args.AddObjectToAsset("tree", vta);
            args.SetMainObject(vta);

            if (!vta.inlineSheet)
                vta.inlineSheet = ScriptableObject.CreateInstance<StyleSheet>();

            // Make sure imported objects aren't editable in the Inspector
            vta.hideFlags = HideFlags.NotEditable;
            vta.inlineSheet.hideFlags = HideFlags.NotEditable;

            args.AddObjectToAsset("inlineStyle", vta.inlineSheet);
        }
    }

    internal class UXMLImporterImpl : StyleValueImporter
    {
        internal struct Error
        {
            public readonly Level level;
            public readonly ImportErrorType error;
            public readonly ImportErrorCode code;
            public readonly object context;
            public readonly string filePath;
            public readonly IXmlLineInfo xmlLineInfo;

            public enum Level
            {
                Info,
                Warning,
                Fatal,
            }

            public Error(ImportErrorType error, ImportErrorCode code, object context, Level level, string filePath,
                         IXmlLineInfo xmlLineInfo)
            {
                this.xmlLineInfo = xmlLineInfo;
                this.error = error;
                this.code = code;
                this.context = context;
                this.level = level;
                this.filePath = filePath;
            }

            private static string ErrorMessage(ImportErrorCode errorCode)
            {
                switch (errorCode)
                {
                    case ImportErrorCode.InvalidXml:
                        return "Xml is not valid, exception during parsing: {0}";
                    case ImportErrorCode.InvalidRootElement:
                        return "Expected the XML Root element name to be '" + k_RootNode + "', found '{0}'";
                    case ImportErrorCode.TemplateHasEmptyName:
                        return "'" + k_TemplateNode + "' declaration requires a non-empty '" + k_TemplateNameAttr +
                            "' attribute";
                    case ImportErrorCode.TemplateInstanceHasEmptySource:
                        return "'" + k_TemplateInstanceNode + "' declaration requires a non-empty '" +
                            k_TemplateInstanceSourceAttr + "' attribute";
                    case ImportErrorCode.MissingPathAttributeOnTemplate:
                        return "'" + k_TemplateNode + "' declaration requires a '" + k_TemplatePathAttr +
                            "' attribute referencing another uxml file";
                    case ImportErrorCode.DuplicateTemplateName:
                        return "Duplicate name '{0}'";
                    case ImportErrorCode.UnknownTemplate:
                        return "Unknown template name '{0}'";
                    case ImportErrorCode.UnknownElement:
                        return "Unknown element name '{0}'";
                    case ImportErrorCode.UnknownAttribute:
                        return "Unknown attribute: '{0}'";
                    case ImportErrorCode.InvalidCssInStyleAttribute:
                        return "USS in 'style' attribute is invalid: {0}";
                    case ImportErrorCode.StyleReferenceEmptyOrMissingPathAttr:
                        return "USS in 'style' attribute is invalid: {0}";
                    case ImportErrorCode.SlotsAreExperimental:
                        return "Slot are an experimental feature. Syntax and semantic may change in the future.";
                    case ImportErrorCode.DuplicateSlotDefinition:
                        return "Slot definition '{0}' is defined more than once";
                    case ImportErrorCode.SlotUsageInNonTemplate:
                        return "Element has an assigned slot, but its parent '{0}' is not a template reference";
                    case ImportErrorCode.SlotDefinitionHasEmptyName:
                        return "Slot definition has an empty name";
                    case ImportErrorCode.SlotUsageHasEmptyName:
                        return "Slot usage has an empty name";
                    case ImportErrorCode.DuplicateContentContainer:
                        return "'contentContainer' attribute must be defined once at most";
                    case ImportErrorCode.DeprecatedAttributeName:
                        return "'{0}' attribute name is deprecated";
                    case ImportErrorCode.ReplaceByAttributeName:
                        return "Please use '{0}' instead";
                    case ImportErrorCode.AttributeOverridesMissingElementNameAttr:
                        return "AttributeOverrides node missing 'element-name' attribute.";
                    default:
                        throw new ArgumentOutOfRangeException("Unhandled error code " + errorCode);
                }
            }

            public override string ToString()
            {
                string message = ErrorMessage(code);
                string lineInfo = xmlLineInfo == null
                    ? ""
                    : UnityString.Format(" ({0},{1})", xmlLineInfo.LineNumber, xmlLineInfo.LinePosition);
                return UnityString.Format("{0}{1}: {2} - {3}", filePath, lineInfo, error,
                    UnityString.Format(message, context == null ? "<null>" : context.ToString()));
            }
        }

        internal class DefaultLogger
        {
            protected List<Error> m_Errors = new List<Error>();
            protected string m_Path;

            internal virtual void LogError(ImportErrorType error, ImportErrorCode code, object context,
                Error.Level level, IXmlLineInfo xmlLineInfo)
            {
                m_Errors.Add(new Error(error, code, context, level, m_Path, xmlLineInfo));
            }

            internal virtual void BeginImport(string path)
            {
                m_Path = path;
            }

            private void LogError(VisualTreeAsset obj, Error error)
            {
                try
                {
                    switch (error.level)
                    {
                        case Error.Level.Info:
                            Debug.LogFormat(obj, error.ToString());
                            break;
                        case Error.Level.Warning:
                            Debug.LogWarningFormat(obj, error.ToString());
                            break;
                        case Error.Level.Fatal:
                            Debug.LogErrorFormat(obj, error.ToString());
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(error));
                    }
                }
                catch (FormatException)
                {
                    switch (error.level)
                    {
                        case Error.Level.Info:
                            Debug.Log(error.ToString());
                            break;
                        case Error.Level.Warning:
                            Debug.LogWarning(error.ToString());
                            break;
                        case Error.Level.Fatal:
                            Debug.LogError(error.ToString());
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(error));
                    }
                }
            }

            internal virtual void FinishImport()
            {
                Dictionary<string, VisualTreeAsset> cache = new Dictionary<string, VisualTreeAsset>();

                foreach (var error in m_Errors)
                {
                    VisualTreeAsset obj;
                    if (!cache.TryGetValue(error.filePath, out obj))
                        cache.Add(error.filePath, obj = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(error.filePath));

                    LogError(obj, error);
                }

                m_Errors.Clear();
            }
        }

        const StringComparison k_Comparison = StringComparison.InvariantCulture;
        public const string k_RootNode = "UXML";
        const string k_TemplateNode = "Template";
        const string k_TemplateNameAttr = "name";
        const string k_TemplatePathAttr = "path";
        const string k_TemplateInstanceNode = "Instance";
        const string k_TemplateInstanceSourceAttr = "template";
        const string k_StyleReferenceNode = "Style";
        const string k_StylePathAttr = "path";
        const string k_SlotDefinitionAttr = "slot-name";
        const string k_SlotUsageAttr = "slot";
        const string k_AttributeOverridesNode = "AttributeOverrides";
        const string k_AttributeOverridesElementNameAttr = "element-name";

        internal UXMLImporterImpl()
        {
        }

        public UXMLImporterImpl(AssetImportContext context) : base(context)
        {
        }

        public void Import(out VisualTreeAsset asset)
        {
            // TODO where is the EndImport matching this?
            logger.BeginImport(assetPath);
            ImportXml(assetPath, out asset);
        }

        // This variable is overriden during editor tests
        internal static DefaultLogger logger = new DefaultLogger();

        void ImportXml(string xmlPath, out VisualTreeAsset vta)
        {
            vta = ScriptableObject.CreateInstance<VisualTreeAsset>();
            vta.visualElementAssets = new List<VisualElementAsset>();
            vta.templateAssets = new List<TemplateAsset>();

            var h = new Hash128();
            using (var stream = File.OpenRead(xmlPath))
            {
                int readCount = 0;
                byte[] b = new byte[1024 * 16];
                while ((readCount = stream.Read(b, 0, b.Length)) > 0)
                {
                    for (int i = readCount; i < b.Length; i++)
                    {
                        b[i] = 0;
                    }
                    Hash128 blockHash = new Hash128();
                    HashUtilities.ComputeHash128(b, ref blockHash);
                    HashUtilities.AppendHash(ref blockHash, ref h);
                }
            }

            vta.contentHash = h.GetHashCode();

            XDocument doc;

            try
            {
                doc = XDocument.Load(xmlPath, LoadOptions.SetLineInfo);
            }
            catch (Exception e)
            {
                logger.LogError(ImportErrorType.Syntax, ImportErrorCode.InvalidXml, e, Error.Level.Fatal, null);
                return;
            }

            LoadXmlRoot(doc, vta);

            StyleSheet inlineSheet = ScriptableObject.CreateInstance<StyleSheet>();
            inlineSheet.name = "inlineStyle";
            m_Builder.BuildTo(inlineSheet);
            vta.inlineSheet = inlineSheet;
        }

        internal void ImportXmlFromString(string xml, out VisualTreeAsset vta)
        {
            vta = ScriptableObject.CreateInstance<VisualTreeAsset>();
            vta.visualElementAssets = new List<VisualElementAsset>();
            vta.templateAssets = new List<TemplateAsset>();

            var h = new Hash128();
            byte[] b = Encoding.UTF8.GetBytes(xml);
            HashUtilities.ComputeHash128(b, ref h);
            vta.contentHash = h.GetHashCode();

            XDocument doc;

            try
            {
                doc = XDocument.Parse(xml, LoadOptions.SetLineInfo);
            }
            catch (Exception e)
            {
                logger.LogError(ImportErrorType.Syntax, ImportErrorCode.InvalidXml, e, Error.Level.Fatal, null);
                return;
            }

            LoadXmlRoot(doc, vta);

            StyleSheet inlineSheet = ScriptableObject.CreateInstance<StyleSheet>();
            inlineSheet.name = "inlineStyle";
            m_Builder.BuildTo(inlineSheet);
            vta.inlineSheet = inlineSheet;
        }

        void LoadXmlRoot(XDocument doc, VisualTreeAsset vta)
        {
            XElement elt = doc.Root;
            if (!string.Equals(elt.Name.LocalName, k_RootNode, k_Comparison))
            {
                logger.LogError(ImportErrorType.Semantic,
                    ImportErrorCode.InvalidRootElement,
                    elt.Name,
                    Error.Level.Fatal,
                    elt);
                return;
            }

            int orderInDocument = -1;

            foreach (var child in elt.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case k_TemplateNode:
                        LoadTemplateNode(vta, elt, child);
                        break;
                    default:
                        ++orderInDocument;
                        LoadXml(child, null, vta, orderInDocument);
                        continue;
                }
            }
        }

        void LoadTemplateNode(VisualTreeAsset vta, XElement elt, XElement child)
        {
            bool hasPath = false;
            string name = null;
            string path = null;
            foreach (var xAttribute in child.Attributes())
            {
                switch (xAttribute.Name.LocalName)
                {
                    case k_TemplatePathAttr:
                        hasPath = true;
                        path = xAttribute.Value;
                        break;
                    case k_TemplateNameAttr:
                        name = xAttribute.Value;
                        if (String.IsNullOrEmpty(name))
                        {
                            logger.LogError(ImportErrorType.Semantic,
                                ImportErrorCode.TemplateHasEmptyName,
                                child,
                                Error.Level.Fatal,
                                child
                            );
                        }
                        break;
                    default:
                        logger.LogError(ImportErrorType.Semantic,
                            ImportErrorCode.UnknownAttribute,
                            xAttribute.Name.LocalName,
                            Error.Level.Fatal,
                            child
                        );
                        break;
                }
            }

            if (!hasPath)
            {
                logger.LogError(ImportErrorType.Semantic,
                    ImportErrorCode.MissingPathAttributeOnTemplate,
                    null,
                    Error.Level.Fatal,
                    elt
                );
                return;
            }

            if (String.IsNullOrEmpty(name))
                name = Path.GetFileNameWithoutExtension(path);

            if (vta.TemplateExists(name))
            {
                logger.LogError(ImportErrorType.Semantic,
                    ImportErrorCode.DuplicateTemplateName,
                    name,
                    Error.Level.Fatal,
                    elt
                );
                return;
            }

            vta.RegisterTemplate(name, path);
        }

        void LoadXml(XElement elt, VisualElementAsset parent, VisualTreeAsset vta, int orderInDocument)
        {
            VisualElementAsset vea = ResolveType(elt, vta);
            if (vea == null)
            {
                return;
            }

            int parentHash;
            if (parent == null)
            {
                vea.parentId = 0;
                parentHash = vta.GetHashCode();
            }
            else
            {
                vea.parentId = parent.id;
                parentHash = parent.id;
            }

            // id includes the parent id, meaning it's dependent on the whole direct hierarchy
            vea.id = (vta.GetNextChildSerialNumber() + 585386304) * -1521134295 + parentHash;
            vea.orderInDocument = orderInDocument;

            bool startedRule = ParseAttributes(elt, vea, vta, parent);

            // each vea will creates 0 or 1 style rule, with one or more properties
            // they don't have selectors and are directly referenced by index
            // it's then applied during tree cloning
            vea.ruleIndex = startedRule ? m_Builder.EndRule() : -1;
            var templateAsset = vea as TemplateAsset;
            if (templateAsset != null)
                vta.templateAssets.Add(templateAsset);
            else
                vta.visualElementAssets.Add(vea);

            if (elt.HasElements)
            {
                foreach (XElement child in elt.Elements())
                {
                    if (child.Name.LocalName == k_StyleReferenceNode)
                        LoadStyleReferenceNode(vea, child);
                    else if (templateAsset != null && child.Name.LocalName == k_AttributeOverridesNode)
                        LoadAttributeOverridesNode(templateAsset, child);
                    else
                    {
                        ++orderInDocument;
                        LoadXml(child, vea, vta, orderInDocument);
                    }
                }
            }
        }

        void LoadStyleReferenceNode(VisualElementAsset vea, XElement styleElt)
        {
            XAttribute pathAttr = styleElt.Attribute(k_StylePathAttr);
            if (pathAttr == null || String.IsNullOrEmpty(pathAttr.Value))
            {
                logger.LogError(ImportErrorType.Semantic, ImportErrorCode.StyleReferenceEmptyOrMissingPathAttr, null, Error.Level.Warning, styleElt);
                return;
            }
            vea.stylesheets.Add(pathAttr.Value);
        }

        void LoadAttributeOverridesNode(TemplateAsset templateAsset, XElement attributeOverridesElt)
        {
            var elementNameAttr = attributeOverridesElt.Attribute(k_AttributeOverridesElementNameAttr);
            if (elementNameAttr == null || String.IsNullOrEmpty(elementNameAttr.Value))
            {
                logger.LogError(ImportErrorType.Semantic, ImportErrorCode.AttributeOverridesMissingElementNameAttr, null, Error.Level.Warning, attributeOverridesElt);
                return;
            }

            foreach (var attribute in attributeOverridesElt.Attributes())
            {
                if (attribute.Name.LocalName == k_AttributeOverridesElementNameAttr)
                    continue;

                var attributeOverride = new TemplateAsset.AttributeOverride()
                {
                    m_ElementName = elementNameAttr.Value,
                    m_AttributeName = attribute.Name.LocalName,
                    m_Value = attribute.Value
                };

                templateAsset.attributeOverrides.Add(attributeOverride);
            }
        }

        VisualElementAsset ResolveType(XElement elt, VisualTreeAsset visualTreeAsset)
        {
            VisualElementAsset vea = null;

            string elementNamespaceName = elt.Name.NamespaceName;

            if (elementNamespaceName.StartsWith("UnityEditor.Experimental.UIElements") || elementNamespaceName.StartsWith("UnityEngine.Experimental.UIElements"))
            {
                elementNamespaceName = elementNamespaceName.Replace(".Experimental.UIElements", ".UIElements");
            }

            if (elt.Name.LocalName == k_TemplateInstanceNode && elementNamespaceName == "UnityEngine.UIElements")
            {
                XAttribute sourceAttr = elt.Attribute(k_TemplateInstanceSourceAttr);
                if (sourceAttr == null || String.IsNullOrEmpty(sourceAttr.Value))
                {
                    logger.LogError(ImportErrorType.Semantic, ImportErrorCode.TemplateInstanceHasEmptySource, null, Error.Level.Fatal, elt);
                }
                else
                {
                    string templateName = sourceAttr.Value;
                    if (!visualTreeAsset.TemplateExists(templateName))
                    {
                        logger.LogError(ImportErrorType.Semantic, ImportErrorCode.UnknownTemplate, templateName, Error.Level.Fatal, elt);
                    }
                    else
                    {
                        vea = new TemplateAsset(templateName);
                    }
                }
            }
            else
            {
                string fullName = String.IsNullOrEmpty(elementNamespaceName)
                    ? elt.Name.LocalName
                    : elementNamespaceName + "." + elt.Name.LocalName;
                vea = new VisualElementAsset(fullName);
            }

            return vea;
        }

        bool ParseAttributes(XElement elt, VisualElementAsset res, VisualTreeAsset vta, VisualElementAsset parent)
        {
            bool startedRule = false;

            foreach (XAttribute xattr in elt.Attributes())
            {
                string attrName = xattr.Name.LocalName;

                // start with special cases
                switch (attrName)
                {
                    case "class":
                        res.classes = xattr.Value.Split(' ');
                        continue;
                    case "content-container":
                    case "contentContainer":
                        if (attrName == "contentContainer")
                        {
                        }
                        if (vta.contentContainerId != 0)
                        {
                            logger.LogError(ImportErrorType.Semantic, ImportErrorCode.DuplicateContentContainer, null, Error.Level.Fatal, elt);
                            continue;
                        }
                        vta.contentContainerId = res.id;
                        continue;
                    case k_SlotDefinitionAttr:
                        logger.LogError(ImportErrorType.Syntax, ImportErrorCode.SlotsAreExperimental, null, Error.Level.Warning, elt);
                        if (String.IsNullOrEmpty(xattr.Value))
                            logger.LogError(ImportErrorType.Semantic, ImportErrorCode.SlotDefinitionHasEmptyName, null, Error.Level.Fatal, elt);
                        else if (!vta.AddSlotDefinition(xattr.Value, res.id))
                            logger.LogError(ImportErrorType.Semantic, ImportErrorCode.DuplicateSlotDefinition, xattr.Value, Error.Level.Fatal, elt);
                        continue;
                    case k_SlotUsageAttr:
                        logger.LogError(ImportErrorType.Syntax, ImportErrorCode.SlotsAreExperimental, null, Error.Level.Warning, elt);
                        var templateAsset = parent as TemplateAsset;
                        if (templateAsset == null)
                        {
                            logger.LogError(ImportErrorType.Semantic, ImportErrorCode.SlotUsageInNonTemplate, parent, Error.Level.Fatal, elt);
                            continue;
                        }
                        if (string.IsNullOrEmpty(xattr.Value))
                        {
                            logger.LogError(ImportErrorType.Semantic, ImportErrorCode.SlotUsageHasEmptyName, null, Error.Level.Fatal, elt);
                            continue;
                        }
                        templateAsset.AddSlotUsage(xattr.Value, res.id);
                        continue;
                    case "style":
                        ExCSS.StyleSheet parsed = new Parser().Parse("* { " + xattr.Value + " }");
                        if (parsed.Errors.Count != 0)
                        {
                            logger.LogError(
                                ImportErrorType.Semantic,
                                ImportErrorCode.InvalidCssInStyleAttribute,
                                parsed.Errors.Aggregate("", (s, error) => s + error.ToString() + "\n"),
                                Error.Level.Warning,
                                xattr);
                            continue;
                        }
                        if (parsed.StyleRules.Count != 1)
                        {
                            logger.LogError(
                                ImportErrorType.Semantic,
                                ImportErrorCode.InvalidCssInStyleAttribute,
                                "Expected one style rule, found " + parsed.StyleRules.Count,
                                Error.Level.Warning,
                                xattr);
                            continue;
                        }
                        m_Builder.BeginRule(-1);
                        startedRule = true;
                        foreach (Property prop in parsed.StyleRules[0].Declarations)
                        {
                            m_Builder.BeginProperty(prop.Name);
                            VisitValue(prop.Term);
                            m_Builder.EndProperty();
                        }

                        // Don't call m_Builder.EndRule() here, it's done in LoadXml to get the rule index at the same time !
                        continue;
                }

                res.AddProperty(xattr.Name.LocalName, xattr.Value);
            }
            return startedRule;
        }
    }

    internal enum ImportErrorCode
    {
        InvalidRootElement,
        DuplicateTemplateName,
        UnknownTemplate,
        UnknownElement,
        UnknownAttribute,
        InvalidXml,
        InvalidCssInStyleAttribute,
        MissingPathAttributeOnTemplate,
        TemplateHasEmptyName,
        TemplateInstanceHasEmptySource,
        StyleReferenceEmptyOrMissingPathAttr,
        SlotsAreExperimental,
        DuplicateSlotDefinition,
        SlotUsageInNonTemplate,
        SlotDefinitionHasEmptyName,
        SlotUsageHasEmptyName,
        DuplicateContentContainer,
        DeprecatedAttributeName,
        ReplaceByAttributeName,
        AttributeOverridesMissingElementNameAttr
    }

    internal enum ImportErrorType
    {
        Syntax,
        Semantic
    }
}
