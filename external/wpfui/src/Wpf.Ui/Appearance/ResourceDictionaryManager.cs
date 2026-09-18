// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.ObjectModel;
using System.Windows;

namespace Wpf.Ui.Appearance;

/// <summary>
/// Allows managing application dictionaries.
/// </summary>
internal class ResourceDictionaryManager
{
    /// <summary>
    /// Namespace, e.g. the library the resource is being searched for.
    /// </summary>
    public string SearchNamespace { get; }

    public ResourceDictionaryManager(string searchNamespace)
    {
        SearchNamespace = searchNamespace;
    }

    /// <summary>
    /// Shows whether the application contains the <see cref="ResourceDictionary"/>.
    /// </summary>
    /// <param name="resourceLookup">Any part of the resource name.</param>
    /// <returns><see langword="false"/> if it doesn't exist.</returns>
    public bool HasDictionary(string resourceLookup)
    {
        return GetDictionary(resourceLookup) != null;
    }

    /// <summary>
    /// Gets the <see cref="ResourceDictionary"/> if exists.
    /// </summary>
    /// <param name="resourceLookup">Any part of the resource name.</param>
    /// <returns><see cref="ResourceDictionary"/>, <see langword="null"/> if it doesn't exist.</returns>
    public ResourceDictionary GetDictionary(string resourceLookup)
    {
        Collection<ResourceDictionary> applicationDictionaries = Application.Current.Resources.MergedDictionaries;

        if (applicationDictionaries.Count == 0)
            return null;

        resourceLookup = resourceLookup.Trim();

        foreach (var t in applicationDictionaries)
        {
            if (Matches(t, resourceLookup))
                return t;

            foreach (var t1 in t!.MergedDictionaries)
            {
                if (Matches(t1, resourceLookup))
                    return t1;
            }
        }

        return null;
    }

    /// <summary>
    /// Shows whether the application contains the <see cref="ResourceDictionary"/>.
    /// </summary>
    /// <param name="resourceLookup">Any part of the resource name.</param>
    /// <param name="newResourceUri">A valid <see cref="Uri"/> for the replaced resource.</param>
    /// <returns></returns>
    public bool UpdateDictionary(string resourceLookup, Uri newResourceUri)
    {
        Collection<ResourceDictionary> applicationDictionaries = Application.Current.Resources.MergedDictionaries;
        if (applicationDictionaries.Count == 0)
            return false;

        if (newResourceUri == null)
            return false;

        resourceLookup = resourceLookup.Trim();

        for (int i = 0; i < applicationDictionaries.Count; i++)
        {
            if (Matches(applicationDictionaries[i], resourceLookup))
            {
                applicationDictionaries[i] = new() { Source = newResourceUri };

                return true;
            }

            for (int j = 0; j < applicationDictionaries[i].MergedDictionaries.Count; j++)
            {
                if (!Matches(applicationDictionaries[i].MergedDictionaries[j], resourceLookup))
                    continue;

                applicationDictionaries[i].MergedDictionaries[j] = new() { Source = newResourceUri };

                return true;
            }
        }

        return false;
    }

    private bool Matches(ResourceDictionary dictionary, string resourceLookup)
    {
        string source = dictionary?.Source?.OriginalString;

        return source != null &&
               source.Contains(SearchNamespace, StringComparison.OrdinalIgnoreCase) &&
               source.Contains(resourceLookup, StringComparison.OrdinalIgnoreCase);
    }
}
