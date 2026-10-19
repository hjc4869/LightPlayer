using LightStudio.NativeBindings.Types;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim
{
    unsafe class AVDictionaryEntryReference
    {
        AVDictionaryReference _dictionary;
        public AVDictionaryEntry* Entry;
        public string Key
        {
            get
            {
                return Utils.NullTerminatedUTF8StringToString((sbyte*)Entry->key);
            }
        }
        public string Value
        {
            get
            {
                return Utils.NullTerminatedUTF8StringToString((sbyte*)Entry->value);
            }
        }
        public AVDictionaryEntryReference(AVDictionaryReference dictionary, AVDictionaryEntry* entry)
        {
            _dictionary = dictionary;
            Entry = entry;
        }
    }
    unsafe partial class AVDictionaryReference
    {
        ~AVDictionaryReference()
        {
            fixed (AVDictionary** _da = &Dictionary)
            {
                av_dict_free(_da);
            }
        }
        public AVDictionary* Dictionary;
        public int Count { get { return av_dict_count(Dictionary); } }
        public string this[string key]
        {
            get
            {
                var entry = av_dict_get(Dictionary, key, null, 0);
                if (entry == null)
                    return null;
                return Utils.NullTerminatedUTF8StringToString((sbyte*)entry->value);
            }
        }
        public AVDictionaryReference(AVDictionary* dictionary)
        {
            fixed (AVDictionary** _da = &Dictionary)
            {
                av_dict_copy(_da, dictionary, 0);
            }
        }
        public AVDictionaryEntryReference Get(string key, AVDictionaryEntryReference prev, int flags)
        {
            var entry = av_dict_get(Dictionary, key, prev == null ? null : prev.Entry, flags);
            if (entry == null)
                return null;
            else
                return new AVDictionaryEntryReference(this, entry);
        }
    }
    public partial class AVDictionaryWrapper : IReadOnlyDictionary<string, string>
    {
        private AVDictionaryReference _dictionary;

        unsafe public AVDictionaryWrapper(AVDictionary* dictionary)
        {
            _dictionary = new AVDictionaryReference(dictionary);
        }
        public string this[string key]
        {
            get
            {
                return _dictionary[key];
            }
        }
        public int Count
        {
            get
            {
                return _dictionary.Count;
            }
        }
        public IEnumerable<string> Keys
        {
            get
            {
                AVDictionaryEntryReference current = null;
                do
                {
                    current = _dictionary.Get("", current, AV_DICT_IGNORE_SUFFIX);
                    if (current != null)
                        yield return current.Key;
                }
                while (current != null);
            }
        }
        public IEnumerable<string> Values
        {
            get
            {
                AVDictionaryEntryReference current = null;
                do
                {
                    current = _dictionary.Get("", current, AV_DICT_IGNORE_SUFFIX);
                    if (current != null)
                        yield return current.Value;
                }
                while (current != null);
            }
        }
        public bool ContainsKey(string key)
        {
            if (this[key] == null)
                return false;
            return true;
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            AVDictionaryEntryReference current = null;
            do
            {
                current = _dictionary.Get("", current, AV_DICT_IGNORE_SUFFIX);
                if (current != null)
                    yield return new KeyValuePair<string, string>(current.Key, current.Value);
            }
            while (current != null);
        }
        public bool TryGetValue(string key, out string value)
        {
            value = this[key];
            if (value == null)
                return false;
            return true;
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            AVDictionaryEntryReference current = null;
            do
            {
                current = _dictionary.Get("", current, AV_DICT_IGNORE_SUFFIX);
                if (current != null)
                    yield return new KeyValuePair<string, string>(current.Key, current.Value);
            }
            while (current != null);
        }
    }
}
