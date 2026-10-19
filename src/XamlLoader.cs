using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Xml;

namespace Lumen
{
    internal static class XamlLoader
    {
        public static FrameworkElement Load(string resourceName)
        {
            using (Stream stream = Open(resourceName))
            {
                using (XmlReader reader = XmlReader.Create(stream))
                {
                    object result = XamlReader.Load(reader);
                    FrameworkElement element = result as FrameworkElement;
                    if (element == null)
                    {
                        throw new InvalidOperationException("XAML root is not a FrameworkElement: " + resourceName);
                    }
                    return element;
                }
            }
        }

        public static ResourceDictionary LoadDictionary(string resourceName)
        {
            using (Stream stream = Open(resourceName))
            {
                using (XmlReader reader = XmlReader.Create(stream))
                {
                    object result = XamlReader.Load(reader);
                    ResourceDictionary dictionary = result as ResourceDictionary;
                    if (dictionary == null)
                    {
                        throw new InvalidOperationException("XAML root is not a ResourceDictionary: " + resourceName);
                    }
                    return dictionary;
                }
            }
        }

        private static Stream Open(string resourceName)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            Stream stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException("Embedded XAML resource was not found: " + resourceName);
            }
            return stream;
        }
    }
}
