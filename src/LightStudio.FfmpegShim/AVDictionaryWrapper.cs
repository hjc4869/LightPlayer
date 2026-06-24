using FFmpeg.AutoGen;
using System.Collections;
using System.Collections.Generic;
using System.Text;

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
    unsafe class AVDictionaryReference
    {
        static AVDictionaryReference()
        {
            FfmpegNativeInitializer.Initialize();
        }

        ~AVDictionaryReference()
        {
            fixed (AVDictionary** _da = &Dictionary)
            {
                ffmpeg.av_dict_free(_da);
            }
        }
        public AVDictionary* Dictionary;
        public int Count { get { return ffmpeg.av_dict_count(Dictionary); } }
        public string this[string key]
        {
            get
            {
                var entry = ffmpeg.av_dict_get(Dictionary, key, null, 0);
                if (entry == null)
                    return null;
                return Utils.NullTerminatedUTF8StringToString((sbyte*)entry->value);
            }
        }
        public AVDictionaryReference(AVDictionary* dictionary)
        {
            fixed (AVDictionary** _da = &Dictionary)
            {
                ffmpeg.av_dict_copy(_da, dictionary, 0);
            }
        }
        public AVDictionaryEntryReference Get(string key, AVDictionaryEntryReference prev, int flags)
        {
            var entry = ffmpeg.av_dict_get(Dictionary, key, prev == null ? null : prev.Entry, flags);
            if (entry == null)
                return null;
            else
                return new AVDictionaryEntryReference(this, entry);
        }
    }
    //unsafe class AVDictionaryKeysEnumerator : IEnumerator<string>
    //{
    //    AVDictionaryReference _dictionary;
    //    AVDictionaryEntryReference _current;
    //    public AVDictionaryKeysEnumerator(AVDictionaryReference dictionary)
    //    {
    //        _dictionary = dictionary;
    //    }
    //    public string Current
    //    {
    //        get
    //        {
    //            return _current.Key;
    //        }
    //    }
    //    object IEnumerator.Current
    //    {
    //        get
    //        {
    //            return _current.Key;
    //        }
    //    }
    //    public void Dispose()
    //    {

    //    }
    //    public bool MoveNext()
    //    {
    //        var result = _dictionary.Get("", _current, ffmpeg.AV_DICT_IGNORE_SUFFIX);
    //        if (result == null)
    //            return false;
    //        _current = result;
    //        return true;
    //    }
    //    public void Reset()
    //    {
    //        _current = null;
    //    }
    //}
    //unsafe class AVDictionaryKeysEnumerable : IEnumerable<string>
    //{
    //    private AVDictionaryReference _dictionary;
    //    public AVDictionaryKeysEnumerable(AVDictionaryReference dictionary)
    //    {
    //        _dictionary = dictionary;
    //    }
    //    public IEnumerator<string> GetEnumerator()
    //    {
    //        return new AVDictionaryKeysEnumerator(_dictionary);
    //    }
    //    IEnumerator IEnumerable.GetEnumerator()
    //    {
    //        return new AVDictionaryKeysEnumerator(_dictionary);
    //    }
    //}
    //unsafe class AVDictionaryValuesEnumerator : IEnumerator<string>
    //{
    //    AVDictionaryReference _dictionary;
    //    AVDictionaryEntryReference _current;
    //    public AVDictionaryValuesEnumerator(AVDictionaryReference dictionary)
    //    {
    //        _dictionary = dictionary;
    //    }
    //    public string Current
    //    {
    //        get
    //        {
    //            return _current.Value;
    //        }
    //    }
    //    object IEnumerator.Current
    //    {
    //        get
    //        {
    //            return _current.Value;
    //        }
    //    }
    //    public void Dispose()
    //    {

    //    }
    //    public bool MoveNext()
    //    {
    //        var result = _dictionary.Get("", _current, ffmpeg.AV_DICT_IGNORE_SUFFIX);
    //        if (result == null)
    //            return false;
    //        _current = result;
    //        return true;
    //    }
    //    public void Reset()
    //    {
    //        _current = null;
    //    }
    //}
    //unsafe class AVDictionaryValuesEnumerable : IEnumerable<string>
    //{
    //    private AVDictionaryReference _dictionary;
    //    public AVDictionaryValuesEnumerable(AVDictionaryReference dictionary)
    //    {
    //        _dictionary = dictionary;
    //    }
    //    public IEnumerator<string> GetEnumerator()
    //    {
    //        return new AVDictionaryValuesEnumerator(_dictionary);
    //    }
    //    IEnumerator IEnumerable.GetEnumerator()
    //    {
    //        return new AVDictionaryValuesEnumerator(_dictionary);
    //    }
    //}
    //unsafe class AVDictionaryEnumerator : IEnumerator<KeyValuePair<string, string>>
    //{
    //    AVDictionaryReference _dictionary;
    //    AVDictionaryEntryReference _current;
    //    public AVDictionaryEnumerator(AVDictionaryReference dictionary)
    //    {
    //        _dictionary = dictionary;
    //    }
    //    public KeyValuePair<string, string> Current
    //    {
    //        get
    //        {
    //            return new KeyValuePair<string, string>(_current.Key, _current.Value);
    //        }
    //    }
    //    object IEnumerator.Current
    //    {
    //        get
    //        {
    //            return new KeyValuePair<string, string>(_current.Key, _current.Value);
    //        }
    //    }
    //    public void Dispose()
    //    {

    //    }
    //    public bool MoveNext()
    //    {
    //        var result = _dictionary.Get("", _current, ffmpeg.AV_DICT_IGNORE_SUFFIX);
    //        if (result == null)
    //            return false;
    //        _current = result;
    //        return true;
    //    }
    //    public void Reset()
    //    {
    //        _current = null;
    //    }
    //}
    public class AVDictionaryWrapper : IReadOnlyDictionary<string, string>
    {
        private AVDictionaryReference _dictionary;

        static AVDictionaryWrapper()
        {
            FfmpegNativeInitializer.Initialize();
        }

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
                    current = _dictionary.Get("", current, ffmpeg.AV_DICT_IGNORE_SUFFIX);
                    if (current != null)
                        yield return current.Key;
                }
                while (current != null);
                //return new AVDictionaryKeysEnumerable(_dictionary);
            }
        }
        public IEnumerable<string> Values
        {
            get
            {
                AVDictionaryEntryReference current = null;
                do
                {
                    current = _dictionary.Get("", current, ffmpeg.AV_DICT_IGNORE_SUFFIX);
                    if (current != null)
                        yield return current.Value;
                }
                while (current != null);
                //return new AVDictionaryValuesEnumerable(_dictionary);
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
                current = _dictionary.Get("", current, ffmpeg.AV_DICT_IGNORE_SUFFIX);
                if (current != null)
                    yield return new KeyValuePair<string, string>(current.Key, current.Value);
            }
            while (current != null);
            //return new AVDictionaryEnumerator(_dictionary);
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
                current = _dictionary.Get("", current, ffmpeg.AV_DICT_IGNORE_SUFFIX);
                if (current != null)
                    yield return new KeyValuePair<string, string>(current.Key, current.Value);
            }
            while (current != null);
            //return new AVDictionaryEnumerator(_dictionary);
        }
    }
}
