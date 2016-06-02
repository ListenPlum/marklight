﻿#region Using Statements
using UnityEngine;
using System.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Reflection;
using System.Text;
using System.IO;
using MarkLight.Animation;
using MarkLight.Views.UI;
using MarkLight.Views;
using MarkLight.ValueConverters;
#endregion

namespace MarkLight
{
    /// <summary>
    /// Contains logic for accessing and loading view data.
    /// </summary>
    public static class ViewData
    {
        #region Methods

        /// <summary>
        /// Goes through XUML and creates/updates the scene objects.
        /// </summary>
        public static void GenerateViews()
        {
            ViewPresenter.UpdateInstance();
            var viewPresenter = ViewPresenter.Instance;

            viewPresenter.Views.Clear();
            viewPresenter.Views.AddRange(viewPresenter.ViewTypeDataList.Where(y => !y.HideInPresenter).Select(x => x.ViewName).OrderBy(x => x));

            viewPresenter.Themes.Clear();
            viewPresenter.Themes.AddRange(viewPresenter.ThemeData.Select(x => x.ThemeName).OrderBy(x => x));

            // validate views and check for cyclical dependencies
            foreach (var viewType in viewPresenter.ViewTypeDataList)
            {
                viewType.Dependencies.Clear();
                foreach (var dependencyName in viewType.DependencyNames)
                {
                    var dependency = viewPresenter.ViewTypeDataList.Where(x => String.Equals(x.ViewName, dependencyName, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                    if (dependency == null)
                    {
                        Utils.LogError("[MarkLight] {0}: View contains the child view \"{1}\" that could not be found.", viewType.ViewName, dependencyName);
                        continue;
                    }

                    viewType.Dependencies.Add(dependency);
                }
            }

            // sort view type data by dependencies
            try
            {
                viewPresenter.ViewTypeDataList = SortByDependency(viewPresenter.ViewTypeDataList);
            }
            catch (Exception e)
            {
                Utils.LogError("[MarkLight] Unable to generate views. {0}", e.Message);
                return;
            }

            // destroy layout root
            if (viewPresenter.RootView != null)
            {
                GameObject.DestroyImmediate(viewPresenter.RootView);
            }

            // destroy any remaining objects under the view-presenter that should not be there
            if (viewPresenter.transform.childCount > 0)
            {
                for (int i = viewPresenter.transform.childCount - 1; i >= 0; --i)
                {
                    var go = viewPresenter.transform.GetChild(i).gameObject;
                    Utils.LogWarning("[MarkLight] Removed GameObject \"{0}\" under the view-presenter. View-presenter content is reserved for objects generated by the framework.", go.name);
                    GameObject.DestroyImmediate(go);
                }
            }

            // create main view
            if (!String.IsNullOrEmpty(viewPresenter.MainView))
            {
                var mainView = CreateView(viewPresenter.MainView, viewPresenter, viewPresenter);
                if (mainView != null)
                {
                    viewPresenter.RootView = mainView.gameObject;
                }
            }

            // initialize views
            viewPresenter.Initialize();
        }

        /// <summary>
        /// Loads all XUML assets.
        /// </summary>
        public static void LoadAllXuml(IEnumerable<TextAsset> xumlAssets)
        {
            var viewPresenter = ViewPresenter.Instance;

            // clear existing views from view presenter
            viewPresenter.Clear();

            // load xuml
            foreach (var xumlAsset in xumlAssets)
            {
                LoadXuml(xumlAsset);
            }

            // generate views
            GenerateViews();
        }

        /// <summary>
        /// Loads XUML file to the view database.
        /// </summary>
        public static void LoadXuml(TextAsset xumlAsset)
        {
            LoadXuml(xumlAsset.text, xumlAsset.name);
        }

        /// <summary>
        /// Loads XUML string to the view database.
        /// </summary>
        public static void LoadXuml(string xuml, string xumlAssetName = "")
        {
            XElement xumlElement = null;
            try
            {
                xumlElement = XElement.Parse(xuml);
            }
            catch (Exception e)
            {
                Utils.LogError("[MarkLight] {0}: Error parsing XUML. Exception thrown: {1}", xumlAssetName, Utils.GetError(e));
                return;
            }

            // what kind of XUML file is this? 
            if (String.Equals(xumlElement.Name.LocalName, "Theme", StringComparison.OrdinalIgnoreCase))
            {
                // theme
                LoadThemeXuml(xumlElement, xuml, xumlAssetName);
            }
            else if (String.Equals(xumlElement.Name.LocalName, "ResourceDictionary", StringComparison.OrdinalIgnoreCase))
            {
                // resource dictionary
                LoadResourceDictionaryXuml(xumlElement, xuml, xumlAssetName);
            }
            else
            {
                // view
                LoadViewXuml(xumlElement, xuml);
            }
        }

        /// <summary>
        /// Loads XUML to view database.
        /// </summary>
        private static void LoadViewXuml(XElement xumlElement, string xuml)
        {
            var viewPresenter = ViewPresenter.Instance;
            viewPresenter.ViewTypeDataList.RemoveAll(x => String.Equals(x.ViewName, xumlElement.Name.LocalName, StringComparison.OrdinalIgnoreCase));

            var viewTypeData = new ViewTypeData();
            viewPresenter.ViewTypeDataList.Add(viewTypeData);

            viewTypeData.Xuml = xuml;
            viewTypeData.XumlElement = xumlElement;
            viewTypeData.ViewName = xumlElement.Name.LocalName;

            // set dependency names
            foreach (var descendant in xumlElement.Descendants())
            {
                if (!viewTypeData.DependencyNames.Contains(descendant.Name.LocalName, StringComparer.OrdinalIgnoreCase))
                {
                    viewTypeData.DependencyNames.Add(descendant.Name.LocalName);
                }
            }

            // set view type
            var type = GetViewType(viewTypeData.ViewName);
            if (type == null)
            {
                type = typeof(View);
            }

            // set if view is internal
            viewTypeData.HideInPresenter = type.GetCustomAttributes(typeof(HideInPresenter), false).Any();

            // set view action fields
            var viewActionType = typeof(ViewAction);
            var actionFields = type.GetFields().Where(x => x.FieldType == viewActionType).Select(y => y.Name);
            viewTypeData.ViewActionFields.AddRange(actionFields);

            // set dependency fields
            var viewFieldBaseType = typeof(ViewFieldBase);
            var dependencyFields = type.GetFields().Where(x => viewFieldBaseType.IsAssignableFrom(x.FieldType)).Select(y => y.Name);
            viewTypeData.DependencyFields.AddRange(dependencyFields);

            // set component fields
            var componentType = typeof(Component);
            var baseViewType = typeof(View);
            var componentFields = type.GetFields().Where(x => componentType.IsAssignableFrom(x.FieldType) &&
                !baseViewType.IsAssignableFrom(x.FieldType)).Select(y => y.Name);
            viewTypeData.ComponentFields.AddRange(componentFields);

            // set reference fields
            var referenceFields = type.GetFields().Where(x => baseViewType.IsAssignableFrom(x.FieldType) &&
                x.Name != "Parent" && x.Name != "LayoutParent").Select(y => y.Name);
            viewTypeData.ReferenceFields.AddRange(referenceFields);
            viewTypeData.ReferenceFields.Add("GameObject");

            // set excluded component fields
            var excludedComponentFields = type.GetCustomAttributes(typeof(ExcludeComponent), true);
            viewTypeData.ExcludedComponentFields.AddRange(excludedComponentFields.Select(x => (x as ExcludeComponent).ComponentFieldName));

            // set mapped fields and their converters and change handlers
            var mapFields = type.GetFields().SelectMany(x => x.GetCustomAttributes(typeof(MapViewField), true));
            var mapClassFields = type.GetCustomAttributes(typeof(MapViewField), true);
            viewTypeData.MapViewFields.AddRange(mapFields.Select(x => (x as MapViewField).MapFieldData));
            viewTypeData.MapViewFields.AddRange(mapClassFields.Select(x => (x as MapViewField).MapFieldData));

            // .. add mapped dependency fields
            foreach (var field in type.GetFields())
            {
                var mapTo = field.GetCustomAttributes(typeof(MapTo), true).FirstOrDefault() as MapTo;
                if (mapTo == null)
                    continue;

                mapTo.MapFieldData.From = field.Name;
                viewTypeData.MapViewFields.Add(mapTo.MapFieldData);
            }

            //  .. init change handlers and value converters
            foreach (var mapField in viewTypeData.MapViewFields)
            {
                if (mapField.ValueConverterTypeSet)
                {
                    viewTypeData.ViewFieldConverters.Add(new ViewFieldConverterData { ValueConverterType = mapField.ValueConverterType, ViewField = mapField.To });
                }

                if (mapField.ChangeHandlerNameSet)
                {
                    viewTypeData.ViewFieldChangeHandlers.Add(new ViewFieldChangeHandler
                    {
                        ChangeHandlerName = mapField.ChangeHandlerName,
                        ViewField = mapField.To,
                        TriggerImmediately = mapField.TriggerChangeHandlerImmediately
                    });
                }
            }

            // set view field converters and change handlers
            foreach (var field in type.GetFields())
            {
                var valueConverter = field.GetCustomAttributes(typeof(ValueConverter), true).FirstOrDefault();
                if (valueConverter != null)
                {
                    viewTypeData.ViewFieldConverters.Add(new ViewFieldConverterData { ViewField = field.Name, ValueConverterType = valueConverter.GetType().Name });
                }

                var changeHandler = field.GetCustomAttributes(typeof(ChangeHandler), true).FirstOrDefault() as ChangeHandler;
                if (changeHandler != null)
                {
                    viewTypeData.ViewFieldChangeHandlers.Add(new ViewFieldChangeHandler { ViewField = field.Name, ChangeHandlerName = changeHandler.Name, TriggerImmediately = changeHandler.TriggerImmediately });
                }

                var notNotSetFromXuml = field.GetCustomAttributes(typeof(NotSetFromXuml), true).FirstOrDefault() as NotSetFromXuml;
                if (notNotSetFromXuml != null)
                {
                    viewTypeData.FieldsNotSetFromXuml.Add(field.Name);
                }
            }

            // get the normal fields that aren't mapped
            var fields = type.GetFields().Where(x =>
                !viewTypeData.FieldsNotSetFromXuml.Contains(x.Name) &&
                !viewTypeData.ReferenceFields.Contains(x.Name) &&
                !viewTypeData.ComponentFields.Contains(x.Name) &&
                !viewTypeData.ViewActionFields.Contains(x.Name) &&
                !viewTypeData.DependencyFields.Contains(x.Name) &&
                !x.IsStatic
            ).Select(y => y.Name);
            var properties = type.GetProperties().Where(x =>
                !viewTypeData.FieldsNotSetFromXuml.Contains(x.Name) &&
                !viewTypeData.ReferenceFields.Contains(x.Name) &&
                !viewTypeData.ComponentFields.Contains(x.Name) &&
                !viewTypeData.ViewActionFields.Contains(x.Name) &&
                !viewTypeData.DependencyFields.Contains(x.Name) &&
                x.GetSetMethod() != null &&
                x.GetGetMethod() != null &&
                x.Name != "enabled" &&
                x.Name != "useGUILayout" &&
                x.Name != "tag" &&
                x.Name != "hideFlags" &&
                x.Name != "name"
            ).Select(y => y.Name);
            viewTypeData.ViewFields.AddRange(fields);
            viewTypeData.ViewFields.AddRange(properties);
        }

        /// <summary>
        /// Loads XUML to theme database.
        /// </summary>
        private static void LoadThemeXuml(XElement xumlElement, string xuml, string xumlAssetName)
        {
            var viewPresenter = ViewPresenter.Instance;

            var themeNameAttr = xumlElement.Attribute("Name");
            if (themeNameAttr == null)
            {
                Utils.LogError("[MarkLight] {0}: Error parsing theme XUML. Name attribute missing.", xumlAssetName);
            }

            viewPresenter.ThemeData.RemoveAll(x => String.Equals(x.ThemeName, themeNameAttr.Value, StringComparison.OrdinalIgnoreCase));

            var themeData = new ThemeData();
            viewPresenter.ThemeData.Add(themeData);

            themeData.Xuml = xuml;
            themeData.XumlElement = xumlElement;
            themeData.ThemeName = themeNameAttr.Value;

            var baseDirectoryAttr = xumlElement.Attribute("BaseDirectory");
            themeData.BaseDirectorySet = baseDirectoryAttr != null;
            if (themeData.BaseDirectorySet)
            {
                themeData.BaseDirectory = baseDirectoryAttr.Value;
            }
            
            var unitSizeAttr = xumlElement.Attribute("UnitSize");
            themeData.UnitSizeSet = unitSizeAttr != null;
            if (themeData.UnitSizeSet)
            {           
                if (String.IsNullOrEmpty(unitSizeAttr.Value))
                {
                    // use default unit size
                    themeData.UnitSize = ViewPresenter.Instance.UnitSize;
                }
                else
                {
                    var converter = new Vector3ValueConverter();
                    var result = converter.Convert(unitSizeAttr.Value);
                    if (result.Success)
                    {
                        themeData.UnitSize = (Vector3)result.ConvertedValue;
                    }
                    else
                    {
                        Utils.LogError("[MarkLight] {0}: Error parsing theme XUML. Unable to parse UnitSize attribute value \"{1}\".", xumlAssetName, unitSizeAttr.Value);
                        themeData.UnitSize = ViewPresenter.Instance.UnitSize;
                    }
                }
            }

            // load theme elements
            foreach (var childElement in xumlElement.Elements())
            {
                var themeElement = new ThemeElementData();
                themeElement.ViewName = childElement.Name.LocalName;

                var idAttr = childElement.Attribute("Id");
                if (idAttr != null)
                {
                    themeElement.Id = idAttr.Value;
                }

                var styleAttr = childElement.Attribute("Style");
                if (styleAttr != null)
                {
                    themeElement.Style = styleAttr.Value;
                }

                var basedOnAttr = childElement.Attribute("BasedOn");
                if (basedOnAttr != null)
                {
                    themeElement.BasedOn = basedOnAttr.Value;
                }

                themeElement.XumlElement = childElement;
                themeElement.Xuml = childElement.ToString();

                themeData.ThemeElementData.Add(themeElement);
            }
        }

        /// <summary>
        /// Loads XUML to resource dictionary database.
        /// </summary>
        private static void LoadResourceDictionaryXuml(XElement xumlElement, string xuml, string xumlAssetName)
        {            
            var viewPresenter = ViewPresenter.Instance;
            var dictionaryNameAttr = xumlElement.Attribute("Name");
            string dictionaryName = dictionaryNameAttr != null ? dictionaryNameAttr.Value : "Default";

            // see if dictionary exist otherwise create a new one
            ResourceDictionary resourceDictionary = viewPresenter.ResourceDictionaries.FirstOrDefault(x => String.Equals(x.Name, dictionaryName, StringComparison.OrdinalIgnoreCase));
            if (resourceDictionary == null)
            {
                resourceDictionary = new ResourceDictionary();
                viewPresenter.ResourceDictionaries.Add(resourceDictionary);
            }

            resourceDictionary.Name = dictionaryName;
            resourceDictionary.Xuml = xuml;
            resourceDictionary.XumlElement = xumlElement;

            // load resources
            var resources = LoadResourceXuml(xumlAssetName, xumlElement, null, null, null, null);
            resourceDictionary.AddResources(resources);
        }

        /// <summary>
        /// Loads resources from a resource element.
        /// </summary>
        private static List<Resource> LoadResourceXuml(string xumlAssetName, XElement xumlElement, string parentKey, string parentValue, string parentLanguage, string parentPlatform)
        {
            var resources = new List<Resource>();
            foreach (var childElement in xumlElement.Elements())
            {
                var resource = new Resource();
                if (childElement.Name.LocalName == "Resource")
                {
                    var keyAttr = childElement.Attribute("Key");
                    resource.Key = keyAttr != null ? keyAttr.Value : parentKey;

                    var valueAttr = childElement.Attribute("Value");
                    resource.Value = valueAttr != null ? valueAttr.Value : parentValue;

                    var languageAttr = childElement.Attribute("Language");
                    resource.Language = languageAttr != null ? languageAttr.Value : parentLanguage;

                    var platformAttr = childElement.Attribute("Platform");
                    resource.Platform = platformAttr != null ? platformAttr.Value : parentPlatform;
                }
                else if (childElement.Name.LocalName == "ResourceGroup")
                {
                    var keyAttr = childElement.Attribute("Key");
                    string key = null;
                    string value = null;
                    string language = null;
                    string platform = null;

                    if (keyAttr != null)
                    {
                        key = keyAttr.Value;
                    }

                    var valueAttr = childElement.Attribute("Value");
                    if (valueAttr != null)
                    {
                        value = valueAttr.Value;
                    }

                    var languageAttr = childElement.Attribute("Language");
                    if (languageAttr != null)
                    {
                        language = languageAttr.Value;
                    }

                    var platformAttr = childElement.Attribute("Platform");
                    if (platformAttr != null)
                    {
                        platform = platformAttr.Value;
                    }

                    var childResources = LoadResourceXuml(xumlAssetName, childElement, key ?? parentKey, value ?? parentValue,
                        language ?? parentLanguage, platform ?? parentPlatform);
                    resources.AddRange(childResources);
                }
                else
                {
                    Utils.LogError("[MarkLight] {0}: Error parsing resource dictionary XUML. Unrecognizable element \"{1}\".", xumlAssetName, childElement.Name.LocalName);
                    continue;
                }

                resources.Add(resource);
            }

            return resources;
        }

        /// <summary>
        /// Creates view of specified type.
        /// </summary>
        public static T CreateView<T>(View layoutParent, View parent, ValueConverterContext context = null, string themeName = "", string id = "", string style = "", IEnumerable<XElement> contentXuml = null) where T : View
        {
            Type viewType = typeof(T);
            return CreateView(viewType.Name, layoutParent, parent, context, themeName, id, style, contentXuml) as T;
        }

        /// <summary>
        /// Creates view of specified type.
        /// </summary>
        public static View CreateView(string viewName, View layoutParent, View parent, ValueConverterContext context = null, string theme = "", string id = "", string style = "", IEnumerable<XElement> contentXuml = null)
        {
            // Creates the views in the following order:
            // CreateView(view)
            //   Foreach child
            //     CreateView(child)
            //     SetViewValues(child)
            //   Foreach contentView
            //      CreateView(contentView)
            //      SetViewValues(contentView)
            //   SetViewValues(view)       
            //   SetThemeValues(view)

            // TODO store away and re-use view templates

            // use default theme if no theme is specified
            if (String.IsNullOrEmpty(theme))
            {
                theme = ViewPresenter.Instance.DefaultTheme;
            }

            // initialize value converter context
            if (context == null)
            {
                context = ValueConverterContext.Default;
            }
                        
            // create view from XUML
            var viewTypeData = GetViewTypeData(viewName);
            if (viewTypeData == null)
            {
                return null;
            }

            // get view type
            var viewType = GetViewType(viewName);
            if (viewType == null)
            {
                viewType = typeof(View);
            }

            // create view game object with required components
            var go = new GameObject(viewTypeData.ViewName);
            if (typeof(UIView).IsAssignableFrom(viewType))
            {
                go.AddComponent<RectTransform>();
            }
            go.transform.SetParent(layoutParent.transform, false);

            // create view behavior and initialize it
            var view = go.AddComponent(viewType) as View;
            view.LayoutParent = layoutParent;
            view.Parent = parent;
            view.Id = id;
            view.Style = style;
            view.Theme = theme;
            view.Content = view;
            view.ViewXumlName = viewName;
            view.ValueConverterContext = context;

            // set component fields
            foreach (var componentField in viewTypeData.ComponentFields)
            {
                if (viewTypeData.ExcludedComponentFields.Contains(componentField))
                    continue; // exclude component

                var componentFieldInfo = viewType.GetField(componentField);
                Component component = null;
                if (componentField == "Transform")
                {
                    component = go.transform;
                }
                else if (componentField == "RectTransform")
                {
                    component = go.transform as RectTransform;
                }
                else
                {
                    component = go.AddComponent(componentFieldInfo.FieldType);
                }
                componentFieldInfo.SetValue(view, component);
            }

            // set view action fields
            foreach (var viewActionField in viewTypeData.ViewActionFields)
            {
                var viewActionFieldInfo = viewTypeData.GetViewField(viewActionField);
                viewActionFieldInfo.SetValue(view, new ViewAction(viewActionField));
            }

            // set dependency fields            
            foreach (var dependencyField in viewTypeData.DependencyFields)
            {
                var dependencyFieldInfo = viewTypeData.GetViewField(dependencyField);
                var dependencyFieldInstance = TypeHelper.CreateInstance(dependencyFieldInfo.FieldType) as ViewFieldBase;
                dependencyFieldInfo.SetValue(view, dependencyFieldInstance);
                dependencyFieldInstance.ParentView = view;
                dependencyFieldInstance.ViewFieldPath = dependencyField;
                dependencyFieldInstance.IsMapped = !String.Equals(viewTypeData.GetMappedViewField(dependencyField), dependencyField);
            }

            // parse child XUML and for each child create views and set their values
            foreach (var childElement in viewTypeData.XumlElement.Elements())
            {
                var childViewIdAttr = childElement.Attribute("Id");
                var childViewStyleAttr = childElement.Attribute("Style");
                var childThemeAttr = childElement.Attribute("Theme");
                var childContext = GetValueConverterContext(context, childElement, view.GameObjectName);

                var childView = CreateView(childElement.Name.LocalName, view, view, childContext,
                    childThemeAttr != null ? childThemeAttr.Value : theme,
                    childViewIdAttr != null ? childViewIdAttr.Value : String.Empty,
                    GetChildViewStyle(view.Style, childViewStyleAttr),
                    childElement.Elements());
                SetViewValues(childView, childElement, view, childContext);
            }

            // search for a content placeholder
            ContentPlaceholder contentContainer = view.Find<ContentPlaceholder>(true, view);
            var contentLayoutParent = view;
            if (contentContainer != null)
            {
                contentLayoutParent = contentContainer.LayoutParent;
                view.Content = contentLayoutParent;

                // remove placeholder
                GameObject.DestroyImmediate(contentContainer.gameObject);
            }

            // parse content XUML and for each content child create views and set their values
            if (contentXuml != null)
            {
                // create content views
                foreach (var contentElement in contentXuml)
                {
                    var contentElementIdAttr = contentElement.Attribute("Id");
                    var contentElementStyleAttr = contentElement.Attribute("Style");
                    var contentThemeAttr = contentElement.Attribute("Theme");
                    var contentContext = GetValueConverterContext(context, contentElement, view.GameObjectName);

                    var contentView = CreateView(contentElement.Name.LocalName, contentLayoutParent, parent, contentContext,
                        contentThemeAttr != null ? contentThemeAttr.Value : theme,
                        contentElementIdAttr != null ? contentElementIdAttr.Value : String.Empty,
                        GetChildViewStyle(view.Style, contentElementStyleAttr),
                        contentElement.Elements());
                    SetViewValues(contentView, contentElement, parent, contentContext);
                }
            }

            // set view references
            foreach (var referenceField in viewTypeData.ReferenceFields)
            {
                // is this a reference to a view?
                var referencedView = view.Find<View>(x => String.Equals(x.Id, referenceField, StringComparison.OrdinalIgnoreCase),
                    true, view);
                if (referencedView != null)
                {
                    var referenceFieldInfo = viewType.GetField(referenceField);
                    referenceFieldInfo.SetValue(view, referencedView);
                }
            }

            // set view default values
            view.SetDefaultValues();

            // set internal view values that appear inside the root element of the XUML file
            SetViewValues(view, viewTypeData.XumlElement, view, context);

            // set theme values
            var themeData = GetThemeData(theme);
            if (themeData != null)
            {
                foreach (var themeElement in themeData.GetThemeElementData(view.ViewTypeName, view.Id, view.Style))
                {
                    var themeValueContext = new ValueConverterContext(context);
                    if (themeData.BaseDirectorySet)
                    {
                        themeValueContext.BaseDirectory = themeData.BaseDirectory;
                    }
                    if (themeData.UnitSizeSet)
                    {
                        themeValueContext.UnitSize = themeData.UnitSize;
                    }

                    SetViewValues(view, themeElement.XumlElement, view, themeValueContext);
                }
            }

            return view;
        }

        /// <summary>
        /// Creates value converter context from element settings.
        /// </summary>
        private static ValueConverterContext GetValueConverterContext(ValueConverterContext parentContext, XElement element, string viewName)
        {
            var elementContext = new ValueConverterContext(parentContext);

            var baseDirectoryAttr = element.Attribute("BaseDirectory");
            var unitSizeAttr = element.Attribute("UnitSize");
            if (baseDirectoryAttr != null)
            {
                elementContext.BaseDirectory = baseDirectoryAttr.Value;                
            }
            if (unitSizeAttr != null)
            {
                var unitSizeString = unitSizeAttr.Value;
                var converter = new Vector3ValueConverter();
                var result = converter.Convert(unitSizeString);
                if (result.Success)
                {
                    elementContext.UnitSize = (Vector3)result.ConvertedValue;
                }
                else
                {
                    Utils.LogError("[MarkLight] {0}: Error parsing XUML. Unable to parse UnitSize attribute value \"{1}\".", viewName, unitSizeString);
                    elementContext.UnitSize = ViewPresenter.Instance.UnitSize;
                }
            }

            return elementContext;
        }

        /// <summary>
        /// Gets child view style based on attribute value.
        /// </summary>
        private static string GetChildViewStyle(string parentStyle, XAttribute childViewStyleAttr)
        {
            var childStyleName = childViewStyleAttr != null ? childViewStyleAttr.Value : String.Empty;
            return childStyleName == "*" ? parentStyle : childStyleName;
        }

        /// <summary>
        /// Sets view values parsed from XUML.
        /// </summary>
        private static void SetViewValues(View view, XElement xumlElement, View parent, ValueConverterContext context)
        {
            if (view == null)
                return;

            var viewTypeData = GetViewTypeData(view.ViewTypeName);
            foreach (var attribute in xumlElement.Attributes())
            {
                string viewFieldPath = attribute.Name.LocalName;
                string viewFieldValue = attribute.Value;

                // ignore namespace specification
                if (String.Equals(viewFieldPath, "xmlns", StringComparison.OrdinalIgnoreCase))
                    continue;

                // check if the field value is allowed to be be set from xuml
                bool notAllowed = viewTypeData.FieldsNotSetFromXuml.Contains(viewFieldPath);
                if (notAllowed)
                {
                    Utils.LogError("[MarkLight] {0}: Unable to assign value \"{1}\" to view field \"{2}.{3}\". Field not allowed to be set from XUML.", view.GameObjectName, viewFieldValue, view.ViewTypeName, viewFieldPath);
                    continue;
                }

                // check if value contains a binding
                if (ViewFieldBinding.ValueHasBindings(viewFieldValue))
                {
                    view.AddBinding(viewFieldPath, viewFieldValue);
                    continue;
                }

                // check if we are setting a state-value
                int stateIndex = viewFieldPath.IndexOf('-', 0);
                if (stateIndex > 0)
                {
                    // check if we are setting a sub-state, i.e. the state of the target view
                    var stateViewField = viewFieldPath.Substring(stateIndex + 1);
                    var state = viewFieldPath.Substring(0, stateIndex);

                    bool isSubState = stateViewField.StartsWith("-");
                    if (isSubState)
                    {
                        stateViewField = stateViewField.Substring(1);
                    }

                    // setting the state of the source view
                    view.AddStateValue(state, stateViewField, attribute.Value, context, isSubState);
                    continue;
                }

                // get view field data
                var viewFieldData = view.GetViewFieldData(viewFieldPath);
                if (viewFieldData == null)
                {
                    Utils.LogError("[MarkLight] {0}: Unable to assign value \"{1}\" to view field \"{2}\". View field not found.", view.GameObjectName, viewFieldValue, viewFieldPath);
                    continue;
                }

                // check if we are setting a view action handler
                if (viewFieldData.ViewFieldTypeName == "ViewAction")
                {
                    viewFieldData.SourceView.AddViewActionEntry(viewFieldData.ViewFieldPath, viewFieldValue, parent);
                    continue;
                }

                // we are setting a normal view field
                view.SetValue(attribute.Name.LocalName, attribute.Value, true, null, context, true);
            }
        }

        /// <summary>
        /// Gets view type data.
        /// </summary>
        public static ViewTypeData GetViewTypeData(string viewTypeName)
        {
            return ViewPresenter.Instance.GetViewTypeData(viewTypeName);
        }

        /// <summary>
        /// Gets theme data.
        /// </summary>
        public static ThemeData GetThemeData(string themeName)
        {
            return ViewPresenter.Instance.GetThemeData(themeName);
        }

        /// <summary>
        /// Gets view type from view type name.
        /// </summary>
        public static Type GetViewType(string viewTypeName)
        {
            return ViewPresenter.Instance.GetViewType(viewTypeName);
        }

        /// <summary>
        /// Gets value converter for view field type.
        /// </summary>
        public static ValueConverter GetValueConverterForType(string viewFieldType)
        {
            return ViewPresenter.Instance.GetValueConverterForType(viewFieldType);
        }

        /// <summary>
        /// Gets value converter.
        /// </summary>
        public static ValueConverter GetValueConverter(string valueConverterTypeName)
        {
            return ViewPresenter.Instance.GetValueConverter(valueConverterTypeName);
        }

        /// <summary>
        /// Gets value interpolator for view field type.
        /// </summary>
        public static ValueInterpolator GetValueInterpolatorForType(string viewFieldType)
        {
            return ViewPresenter.Instance.GetValueInterpolatorForType(viewFieldType);
        }

        /// <summary>
        /// Sorts the view elements by their dependencies so they can be processed in the right order.
        /// </summary>
        private static List<ViewTypeData> SortByDependency(List<ViewTypeData> viewTypeDataList)
        {
            // reset permanent and temporary marks used while sorting
            viewTypeDataList.ForEach(x =>
            {
                x.PermanentMark = false;
                x.TemporaryMark = false;
            });

            var sorted = new List<ViewTypeData>();
            while (viewTypeDataList.Any(x => !x.PermanentMark))
            {
                var viewTypeData = viewTypeDataList.First(x => !x.PermanentMark);
                Visit(viewTypeData, sorted, String.Empty);
            }

            return sorted;
        }

        /// <summary>
        /// Used by dependency sort algorithm.
        /// </summary>
        private static void Visit(ViewTypeData viewTypeData, List<ViewTypeData> sorted, string dependencyChain)
        {
            if (viewTypeData.TemporaryMark)
            {
                // cyclical dependency detected
                throw new Exception(String.Format("Cyclical dependency {0}{1} detected.", dependencyChain, viewTypeData.ViewName));
            }
            else if (!viewTypeData.PermanentMark)
            {
                viewTypeData.TemporaryMark = true;
                foreach (var dependency in viewTypeData.Dependencies)
                {
                    Visit(dependency, sorted, String.Format("{0}{1}->", dependencyChain, viewTypeData.ViewName));
                }
                viewTypeData.TemporaryMark = false;
                viewTypeData.PermanentMark = true;

                // add element to list
                sorted.Add(viewTypeData);
            }
        }

        #endregion
    }
}
