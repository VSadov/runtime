// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace System.Diagnostics
{
    /// <summary>
    /// Provides interaction with Windows event logs.
    /// </summary>
    [DefaultEvent("EntryWritten")]
    public class EventLog : Component, ISupportInitialize
    {
        private const string EventLogKey = "SYSTEM\\CurrentControlSet\\Services\\EventLog";
        internal const string DllName = "EventLogMessages.dll";
        internal const string AltDllName = "System.Diagnostics.EventLog.Messages.dll";
        private const string eventLogMutexName = "netfxeventlog.1.0";
        private const int DefaultMaxSize = 512 * 1024;

        private EventLogInternal _underlyingEventLog;

        public EventLog() : this(string.Empty, ".", string.Empty)
        {
        }

        public EventLog(string logName) : this(logName, ".", string.Empty)
        {
        }

        public EventLog(string logName, string machineName) : this(logName, machineName, string.Empty)
        {
        }

        public EventLog(string logName, string machineName, string source)
        {
            _underlyingEventLog = new EventLogInternal(logName, machineName, source, this);
        }

        /// <summary>
        /// The contents of the log.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public EventLogEntryCollection Entries
        {
            get
            {
                return _underlyingEventLog.Entries;
            }
        }

        [Browsable(false)]
        public string LogDisplayName
        {
            get
            {
                return _underlyingEventLog.LogDisplayName;
            }
        }

        /// <summary>
        /// Gets or sets the name of the log to read from and write to.
        /// </summary>
        [ReadOnly(true)]
        [DefaultValue("")]
        [SettingsBindable(true)]
        public string Log
        {
            get
            {
                return _underlyingEventLog.Log;
            }
            set
            {
                EventLogInternal newLog = new EventLogInternal(value, _underlyingEventLog.MachineName, _underlyingEventLog.Source, this);
                EventLogInternal oldLog = _underlyingEventLog;

                if (oldLog.EnableRaisingEvents)
                {
                    newLog.onEntryWrittenHandler = oldLog.onEntryWrittenHandler;
                    newLog.EnableRaisingEvents = true;
                }

                _underlyingEventLog = newLog;
                oldLog.Close();
            }
        }

        /// <summary>
        /// The machine on which this event log resides.
        /// </summary>
        [ReadOnly(true)]
        [DefaultValue(".")]
        [SettingsBindable(true)]
        public string MachineName
        {
            get
            {
                return _underlyingEventLog.MachineName;
            }
            set
            {
                EventLogInternal newLog = new EventLogInternal(_underlyingEventLog.logName, value, _underlyingEventLog.sourceName, this);
                EventLogInternal oldLog = _underlyingEventLog;

                if (oldLog.EnableRaisingEvents)
                {
                    newLog.onEntryWrittenHandler = oldLog.onEntryWrittenHandler;
                    newLog.EnableRaisingEvents = true;
                }

                _underlyingEventLog = newLog;
                oldLog.Close();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public long MaximumKilobytes
        {
            get => _underlyingEventLog.MaximumKilobytes;
            set => _underlyingEventLog.MaximumKilobytes = value;
        }

        [Browsable(false)]
        public OverflowAction OverflowAction
        {
            get => _underlyingEventLog.OverflowAction;
        }

        [Browsable(false)]
        public int MinimumRetentionDays
        {
            get => _underlyingEventLog.MinimumRetentionDays;
        }

        internal bool ComponentDesignMode
        {
            get => this.DesignMode;
        }

        internal object ComponentGetService(Type service)
        {
            return GetService(service);
        }

        /// <summary>
        /// Indicates if the component monitors the event log for changes.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(false)]
        public bool EnableRaisingEvents
        {
            get => _underlyingEventLog.EnableRaisingEvents;
            set => _underlyingEventLog.EnableRaisingEvents = value;
        }

        /// <summary>
        /// The object used to marshal the event handler calls issued as a result of an EventLog change.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(null)]
        public ISynchronizeInvoke SynchronizingObject
        {
            get => _underlyingEventLog.SynchronizingObject;
            set => _underlyingEventLog.SynchronizingObject = value;
        }

        /// <summary>
        /// The application name (source name) to use when writing to the event log.
        /// </summary>
        [ReadOnly(true)]
        [DefaultValue("")]
        [SettingsBindable(true)]
        public string Source
        {
            get => _underlyingEventLog.Source;
            set
            {
                EventLogInternal newLog = new EventLogInternal(_underlyingEventLog.Log, _underlyingEventLog.MachineName, CheckAndNormalizeSourceName(value), this);
                EventLogInternal oldLog = _underlyingEventLog;

                if (oldLog.EnableRaisingEvents)
                {
                    newLog.onEntryWrittenHandler = oldLog.onEntryWrittenHandler;
                    newLog.EnableRaisingEvents = true;
                }

                _underlyingEventLog = newLog;
                oldLog.Close();
            }
        }

        /// <summary>
        /// Raised each time any application writes an entry to the event log.
        /// </summary>
        public event EntryWrittenEventHandler EntryWritten
        {
            add
            {
                _underlyingEventLog.EntryWritten += value;
            }
            remove
            {
                _underlyingEventLog.EntryWritten -= value;
            }
        }

        public void BeginInit()
        {
            _underlyingEventLog.BeginInit();
        }

        public void Clear()
        {
            _underlyingEventLog.Clear();
        }

        public void Close()
        {
            _underlyingEventLog.Close();
        }

        public static void CreateEventSource(string source, string logName)
        {
            CreateEventSource(new EventSourceCreationData(source, logName, "."));
        }

        [Obsolete("EventLog.CreateEventSource has been deprecated. Use System.Diagnostics.EventLog.CreateEventSource(EventSourceCreationData sourceData) instead.")]
        public static void CreateEventSource(string source, string logName, string machineName)
        {
            CreateEventSource(new EventSourceCreationData(source, logName, machineName));
        }

        public static void CreateEventSource(EventSourceCreationData sourceData)
        {
            ArgumentNullException.ThrowIfNull(sourceData);

            string logName = sourceData.LogName;
            string source = sourceData.Source;
            string machineName = sourceData.MachineName;

            if (!SyntaxCheck.CheckMachineName(machineName))
            {
                throw new ArgumentException(SR.Format(SR.InvalidParameter, nameof(machineName), machineName));
            }

            if (string.IsNullOrEmpty(logName))
                logName = "Application";
            if (!ValidLogName(logName, false))
                throw new ArgumentException(SR.BadLogName);
            if (string.IsNullOrEmpty(source))
                throw new ArgumentException(SR.Format(SR.MissingParameter, nameof(source)));
            if (source.Length + EventLogKey.Length > 254)
                throw new ArgumentException(SR.Format(SR.ParameterTooLong, nameof(source), 254 - EventLogKey.Length));

            Mutex mutex = null;
            try
            {
                NetFrameworkUtils.EnterMutex(eventLogMutexName, ref mutex);
                if (SourceExists(source, machineName, true))
                {
                    if (".".Equals(machineName))
                        throw new ArgumentException(SR.Format(SR.LocalSourceAlreadyExists, source));
                    else
                        throw new ArgumentException(SR.Format(SR.SourceAlreadyExists, source, machineName));
                }

                RegistryKey baseKey = null;
                RegistryKey eventKey = null;
                RegistryKey logKey = null;
                RegistryKey sourceLogKey = null;
                RegistryKey sourceKey = null;
                try
                {
                    if (machineName == ".")
                        baseKey = Registry.LocalMachine;
                    else
                        baseKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, machineName);

                    eventKey = baseKey.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\EventLog", true);
                    if (eventKey == null)
                    {
                        if (!".".Equals(machineName))
                            throw new InvalidOperationException(SR.Format(SR.RegKeyMissing, "SYSTEM\\CurrentControlSet\\Services\\EventLog", logName, source, machineName));
                        else
                            throw new InvalidOperationException(SR.Format(SR.LocalRegKeyMissing, "SYSTEM\\CurrentControlSet\\Services\\EventLog", logName, source));
                    }

                    logKey = eventKey.OpenSubKey(logName, true);
                    if (logKey == null)
                    {
                        if (logName.Length == 8 && (
                            string.Equals(logName, "AppEvent", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(logName, "SecEvent", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(logName, "SysEvent", StringComparison.OrdinalIgnoreCase)))
                            throw new ArgumentException(SR.Format(SR.InvalidCustomerLogName, logName));
                    }

                    bool createLogKey = (logKey == null);
                    if (createLogKey)
                    {
                        if (SourceExists(logName, machineName, true))
                        {
                            if (".".Equals(machineName))
                                throw new ArgumentException(SR.Format(SR.LocalLogAlreadyExistsAsSource, logName));
                            else
                                throw new ArgumentException(SR.Format(SR.LogAlreadyExistsAsSource, logName, machineName));
                        }

                        logKey = eventKey.CreateSubKey(logName);
                        SetSpecialLogRegValues(logKey);
                        // A source with the same name as the log has to be created
                        // by default. It is the behavior expected by EventLog API.
                        sourceLogKey = logKey.CreateSubKey(logName);
                        SetSpecialSourceRegValues(sourceLogKey, sourceData);
                    }

                    if (logName != source)
                    {
                        if (!createLogKey)
                        {
                            SetSpecialLogRegValues(logKey);
                        }

                        sourceKey = logKey.CreateSubKey(source);
                        SetSpecialSourceRegValues(sourceKey, sourceData);
                    }
                }
                finally
                {
                    baseKey?.Close();
                    eventKey?.Close();
                    logKey?.Close();
                    sourceLogKey?.Close();
                    sourceKey?.Close();
                }
            }
            finally
            {
                mutex?.ReleaseMutex();
                mutex?.Close();
            }
        }

        public static void Delete(string logName)
        {
            Delete(logName, ".");
        }

        public static void Delete(string logName, string machineName)
        {
            if (!SyntaxCheck.CheckMachineName(machineName))
                throw new ArgumentException(SR.Format(SR.InvalidParameterFormat, nameof(machineName)), nameof(machineName));
            if (string.IsNullOrEmpty(logName))
                throw new ArgumentException(SR.NoLogName);
            if (!ValidLogName(logName, false))
                throw new InvalidOperationException(SR.BadLogName);

            RegistryKey eventlogkey = null;

            Mutex mutex = null;
            try
            {
                NetFrameworkUtils.EnterMutex(eventLogMutexName, ref mutex);
                try
                {
                    eventlogkey = GetEventLogRegKey(machineName, true);
                    if (eventlogkey == null)
                    {
                        throw new InvalidOperationException(SR.Format(SR.RegKeyNoAccess, "HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\EventLog", machineName));
                    }

                    using (RegistryKey logKey = eventlogkey.OpenSubKey(logName))
                    {
                        if (logKey == null)
                            throw new InvalidOperationException(SR.Format(SR.MissingLog, logName, machineName));
                        //clear out log before trying to delete it
                        //that way, if we can't delete the log file, no entries will persist because it has been cleared
                        EventLog logToClear = new EventLog(logName, machineName);
                        try
                        {
                            logToClear.Clear();
                        }
                        finally
                        {
                            logToClear.Close();
                        }

                        string filename = null;
                        try
                        {
                            //most of the time, the "File" key does not exist, but we'll still give it a whirl
                            filename = (string)logKey.GetValue("File");
                        }
                        catch { }
                        if (filename != null)
                        {
                            try
                            {
                                File.Delete(filename);
                            }
                            catch { }
                        }
                    }
                    // now delete the registry entry
                    eventlogkey.DeleteSubKeyTree(logName);
                }
                finally
                {
                    eventlogkey?.Close();
                }
            }
            finally
            {
                mutex?.ReleaseMutex();
            }
        }

        public static void DeleteEventSource(string source)
        {
            DeleteEventSource(source, ".");
        }

        public static void DeleteEventSource(string source, string machineName)
        {
            if (!SyntaxCheck.CheckMachineName(machineName))
            {
                throw new ArgumentException(SR.Format(SR.InvalidParameter, nameof(machineName), machineName));
            }

            Mutex mutex = null;
            try
            {
                NetFrameworkUtils.EnterMutex(eventLogMutexName, ref mutex);
                RegistryKey key = null;
                // First open the key read only so we can do some checks.  This is important so we get the same
                // exceptions even if we don't have write access to the reg key.
                using (key = FindSourceRegistration(source, machineName, true))
                {
                    if (key == null)
                    {
                        if (machineName == null)
                            throw new ArgumentException(SR.Format(SR.LocalSourceNotRegistered, source));
                        else
                            throw new ArgumentException(SR.Format(SR.SourceNotRegistered, source, machineName, "HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\EventLog"));
                    }
                    // Check parent registry key (Event Log Name) and if it's equal to source, then throw an exception.
                    // The reason: each log registry key must always contain subkey (i.e. source) with the same name.
                    string keyname = key.Name;
                    int index = keyname.LastIndexOf('\\');
                    if (string.Compare(keyname, index + 1, source, 0, keyname.Length - index, StringComparison.Ordinal) == 0)
                        throw new InvalidOperationException(SR.Format(SR.CannotDeleteEqualSource, source));
                }

                try
                {
                    key = FindSourceRegistration(source, machineName, false);
                    key.DeleteSubKeyTree(source);
                }
                finally
                {
                    key?.Close();
                }
            }
            finally
            {
                mutex?.ReleaseMutex();
            }
        }

        protected override void Dispose(bool disposing)
        {
            _underlyingEventLog?.Dispose(disposing);
            base.Dispose(disposing);
        }

        public void EndInit()
        {
            _underlyingEventLog.EndInit();
        }

        public static bool Exists(string logName)
        {
            return Exists(logName, ".");
        }

        public static bool Exists(string logName, string machineName)
        {
            if (!SyntaxCheck.CheckMachineName(machineName))
                throw new ArgumentException(SR.Format(SR.InvalidParameterFormat, nameof(machineName)));

            if (string.IsNullOrEmpty(logName))
                return false;

            RegistryKey eventkey = null;
            RegistryKey logKey = null;

            try
            {
                eventkey = GetEventLogRegKey(machineName, false);
                if (eventkey == null)
                    return false;

                logKey = eventkey.OpenSubKey(logName, false); // try to find log file key immediately.
                return (logKey != null);
            }
            finally
            {
                eventkey?.Close();
                logKey?.Close();
            }
        }

        private static RegistryKey FindSourceRegistration(string source, string machineName, bool readOnly)
        {
            return FindSourceRegistration(source, machineName, readOnly, false);
        }

        private static RegistryKey FindSourceRegistration(string source, string machineName, bool readOnly, bool wantToCreate)
        {
            if (!string.IsNullOrEmpty(source))
            {
                RegistryKey eventkey = null;
                try
                {
                    eventkey = GetEventLogRegKey(machineName, !readOnly);
                    if (eventkey == null)
                    {
                        // there's not even an event log service on the machine.
                        // or, more likely, we don't have the access to read the registry.
                        return null;
                    }

                    StringBuilder inaccessibleLogs = null;
                    // Most machines will return only { "Application", "System", "Security" },
                    // but you can create your own if you want.
                    string[] logNames = eventkey.GetSubKeyNames();
                    for (int i = 0; i < logNames.Length; i++)
                    {
                        // see if the source is registered in this log.
                        // NOTE: A source name must be unique across ALL LOGS!
                        RegistryKey sourceKey = null;
                        try
                        {
                            RegistryKey logKey = eventkey.OpenSubKey(logNames[i], /*writable*/!readOnly);
                            if (logKey != null)
                            {
                                sourceKey = logKey.OpenSubKey(source, /*writable*/!readOnly);
                                if (sourceKey != null)
                                {
                                    // found it
                                    return logKey;
                                }
                                else
                                {
                                    logKey.Close();
                                }
                            }
                            // else logKey is null, so we don't need to Close it
                        }
                        catch (UnauthorizedAccessException)
                        {
                            if (inaccessibleLogs == null)
                            {
                                inaccessibleLogs = new StringBuilder(logNames[i]);
                            }
                            else
                            {
                                inaccessibleLogs.Append(", ");
                                inaccessibleLogs.Append(logNames[i]);
                            }
                        }
                        catch (SecurityException)
                        {
                            if (inaccessibleLogs == null)
                            {
                                inaccessibleLogs = new StringBuilder(logNames[i]);
                            }
                            else
                            {
                                inaccessibleLogs.Append(", ");
                                inaccessibleLogs.Append(logNames[i]);
                            }
                        }
                        finally
                        {
                            sourceKey?.Close();
                        }
                    }

                    if (inaccessibleLogs != null)
                        throw new SecurityException(SR.Format(wantToCreate ? SR.SomeLogsInaccessibleToCreate : SR.SomeLogsInaccessible, source, machineName, inaccessibleLogs));

                }
                finally
                {
                    eventkey?.Close();
                }
            }

            return null;
        }

        public static EventLog[] GetEventLogs()
        {
            return GetEventLogs(".");
        }

        public static EventLog[] GetEventLogs(string machineName)
        {
            if (!SyntaxCheck.CheckMachineName(machineName))
            {
                throw new ArgumentException(SR.Format(SR.InvalidParameter, nameof(machineName), machineName));
            }

            string[] logNames = null;

            RegistryKey eventkey = null;
            try
            {
                // we figure out what logs are on the machine by looking in the registry.
                eventkey = GetEventLogRegKey(machineName, false);
                if (eventkey == null)
                    // there's not even an event log service on the machine.
                    // or, more likely, we don't have the access to read the registry.
                    throw new InvalidOperationException(SR.Format(SR.RegKeyMissingShort, EventLogKey, machineName));
                // Most machines will return only { "Application", "System", "Security" },
                // but you can create your own if you want.
                logNames = eventkey.GetSubKeyNames();
            }
            finally
            {
                eventkey?.Close();
            }
            // now create EventLog objects that point to those logs
            List<EventLog> logs = new List<EventLog>(logNames.Length);
            for (int i = 0; i < logNames.Length; i++)
            {
                EventLog log = new EventLog(logNames[i], machineName);
                SafeEventLogReadHandle handle = Interop.Advapi32.OpenEventLog(machineName, logNames[i]);

                if (!handle.IsInvalid)
                {
                    handle.Close();
                    logs.Add(log);
                }
                else if (Marshal.GetLastWin32Error() != Interop.Errors.ERROR_INVALID_PARAMETER)
                {
                    // This api should return the list of all event logs present on the system even if the current user can't open the log.
                    // Windows returns ERROR_INVALID_PARAMETER for special keys which were added in RS5+ but do not represent actual event logs.
                    logs.Add(log);
                }
            }

            return logs.ToArray();
        }

        internal static RegistryKey GetEventLogRegKey(string machine, bool writable)
        {
            RegistryKey lmkey = null;

            try
            {
                if (machine.Equals("."))
                {
                    lmkey = Registry.LocalMachine;
                }
                else
                {
                    lmkey = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, machine);
                }
                if (lmkey != null)
                    return lmkey.OpenSubKey(EventLogKey, writable);
            }
            finally
            {
                lmkey?.Close();
            }
            return null;
        }

        [UnconditionalSuppressMessage("SingleFile", "IL3000: Avoid accessing Assembly file path when publishing as a single file",
            Justification = "The code handles if the path is null by calling AppContext.BaseDirectory")]
        internal static string GetDllPath(string machineName)
        {
            string dllPath = Path.Combine(NetFrameworkUtils.GetLatestBuildDllDirectory(machineName), DllName);

            if (machineName == "." && !File.Exists(dllPath))
            {
                // use this assembly directory
                string assmLocation = typeof(EventLog).Assembly.Location;
                if (!string.IsNullOrEmpty(assmLocation))
                {
                    dllPath = Path.Combine(Path.GetDirectoryName(assmLocation), AltDllName);
                }
                else
                {
                    dllPath = Path.Combine(AppContext.BaseDirectory, AltDllName);
                }
            }

            return dllPath;
        }

        public static bool SourceExists(string source)
        {
            return SourceExists(source, ".");
        }

        public static bool SourceExists(string source, string machineName)
        {
            return SourceExists(source, machineName, false);
        }

        internal static bool SourceExists(string source, string machineName, bool wantToCreate)
        {
            if (!SyntaxCheck.CheckMachineName(machineName))
            {
                throw new ArgumentException(SR.Format(SR.InvalidParameter, nameof(machineName), machineName));
            }

            using (RegistryKey keyFound = FindSourceRegistration(source, machineName, true, wantToCreate))
            {
                return (keyFound != null);
            }
        }

        public static string LogNameFromSourceName(string source, string machineName)
        {
            return _InternalLogNameFromSourceName(source, machineName);
        }

        internal static string _InternalLogNameFromSourceName(string source, string machineName)
        {
            using (RegistryKey key = FindSourceRegistration(source, machineName, true))
            {
                if (key == null)
                    return string.Empty;
                else
                {
                    string name = key.Name;
                    int whackPos = name.LastIndexOf('\\');
                    // this will work even if whackPos is -1
                    return name.Substring(whackPos + 1);
                }
            }
        }

        public void ModifyOverflowPolicy(OverflowAction action, int retentionDays)
        {
            _underlyingEventLog.ModifyOverflowPolicy(action, retentionDays);
        }

        public void RegisterDisplayName(string resourceFile, long resourceId)
        {
            _underlyingEventLog.RegisterDisplayName(resourceFile, resourceId);
        }

        private static void SetSpecialLogRegValues(RegistryKey logKey)
        {
            // Set all the default values for this log.  AutoBackupLogfiles only makes sense in
            // Win2000 SP4, WinXP SP1, and Win2003, but it should alright elsewhere.
            // Since we use this method on the existing system logs as well as our own,
            // we need to make sure we don't overwrite any existing values.
            if (logKey.GetValue("MaxSize") == null)
                logKey.SetValue("MaxSize", DefaultMaxSize, RegistryValueKind.DWord);
            if (logKey.GetValue("AutoBackupLogFiles") == null)
                logKey.SetValue("AutoBackupLogFiles", 0, RegistryValueKind.DWord);
        }

        private static void SetSpecialSourceRegValues(RegistryKey sourceLogKey, EventSourceCreationData sourceData)
        {
            if (string.IsNullOrEmpty(sourceData.MessageResourceFile))
                sourceLogKey.SetValue("EventMessageFile", GetDllPath(sourceData.MachineName), RegistryValueKind.ExpandString);
            else
                sourceLogKey.SetValue("EventMessageFile", FixupPath(sourceData.MessageResourceFile), RegistryValueKind.ExpandString);

            if (!string.IsNullOrEmpty(sourceData.ParameterResourceFile))
                sourceLogKey.SetValue("ParameterMessageFile", FixupPath(sourceData.ParameterResourceFile), RegistryValueKind.ExpandString);

            if (!string.IsNullOrEmpty(sourceData.CategoryResourceFile))
            {
                sourceLogKey.SetValue("CategoryMessageFile", FixupPath(sourceData.CategoryResourceFile), RegistryValueKind.ExpandString);
                sourceLogKey.SetValue("CategoryCount", sourceData.CategoryCount, RegistryValueKind.DWord);
            }
        }

        private static string FixupPath(string path)
        {
            if (path[0] == '%')
                return path;
            else
                return Path.GetFullPath(path);
        }

        internal static string TryFormatMessage(SafeLibraryHandle hModule, uint messageNum, string[] insertionStrings)
        {
            if (insertionStrings.Length == 0)
            {
                return UnsafeTryFormatMessage(hModule, messageNum, insertionStrings);
            }

            // If you pass in an empty array UnsafeTryFormatMessage will just pull out the message.
            string formatString = UnsafeTryFormatMessage(hModule, messageNum, Array.Empty<string>());

            if (formatString == null)
            {
                return null;
            }

            int largestNumber = 0;

            StringBuilder sb = null;
            for (int i = 0; i < formatString.Length; i++)
            {
                if (formatString[i] == '%')
                {
                    if (formatString.Length > i + 1)
                    {
                        if (sb is null)
                        {
                            sb = new StringBuilder();
                        }
                        else
                        {
                            sb.Clear();
                        }

                        while (i + 1 < formatString.Length && char.IsDigit(formatString[i + 1]))
                        {
                            sb.Append(formatString[i + 1]);
                            i++;
                        }
                        // move over the non number character that broke us out of the loop
                        i++;

                        if (sb.Length > 0)
                        {
                            int num;
                            if (int.TryParse(sb.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out num))
                            {
                                largestNumber = Math.Max(largestNumber, num);
                            }
                        }
                    }
                }
            }

            // Replacement strings are 1 indexed.
            if (largestNumber > insertionStrings.Length)
            {
                string[] newStrings = new string[largestNumber];
                Array.Copy(insertionStrings, newStrings, insertionStrings.Length);
                for (int i = insertionStrings.Length; i < newStrings.Length; i++)
                {
                    newStrings[i] = "%" + (i + 1);
                }

                insertionStrings = newStrings;
            }

            return UnsafeTryFormatMessage(hModule, messageNum, insertionStrings);
        }

        // FormatMessageW will AV if you don't pass in enough format strings.  If you call TryFormatMessage we ensure insertionStrings
        // is long enough.  You don't want to call this directly unless you're sure insertionStrings is long enough!
        internal static string UnsafeTryFormatMessage(SafeLibraryHandle hModule, uint messageNum, string[] insertionStrings)
        {
            string msg = null;

            int msgLen = 0;
            var buf = new char[1024];
            int flags = Interop.Kernel32.FORMAT_MESSAGE_FROM_HMODULE | Interop.Kernel32.FORMAT_MESSAGE_ARGUMENT_ARRAY;

            IntPtr[] addresses = new IntPtr[insertionStrings.Length];
            GCHandle[] handles = new GCHandle[insertionStrings.Length];
            GCHandle stringsRoot = GCHandle.Alloc(addresses, GCHandleType.Pinned);

            if (insertionStrings.Length == 0)
            {
                flags |= Interop.Kernel32.FORMAT_MESSAGE_IGNORE_INSERTS;
            }

            try
            {
                for (int i = 0; i < handles.Length; i++)
                {
                    handles[i] = GCHandle.Alloc(insertionStrings[i], GCHandleType.Pinned);
                    addresses[i] = handles[i].AddrOfPinnedObject();
                }
                int lastError = Interop.Errors.ERROR_INSUFFICIENT_BUFFER;
                while (msgLen == 0 && lastError == Interop.Errors.ERROR_INSUFFICIENT_BUFFER)
                {
                    msgLen = Interop.Kernel32.FormatMessage(
                        flags,
                        hModule,
                        messageNum,
                        0,
                        buf,
                        buf.Length,
                        addresses);

                    if (msgLen == 0)
                    {
                        lastError = Marshal.GetLastWin32Error();
                        if (lastError == Interop.Errors.ERROR_INSUFFICIENT_BUFFER)
                            buf = new char[buf.Length * 2];
                    }
                }
            }
            catch
            {
                msgLen = 0;              // return empty on failure
            }
            finally
            {
                for (int i = 0; i < handles.Length; i++)
                {
                    if (handles[i].IsAllocated)
                        handles[i].Free();
                }
                stringsRoot.Free();
            }

            if (msgLen > 0)
            {
                msg = msgLen > 1 && buf[msgLen - 1] == '\n' ?
                    new string(buf, 0, msgLen - 2) : // chop off a single CR/LF pair from the end if there is one. FormatMessage always appends one extra.
                    new string(buf, 0, msgLen);
            }

            return msg;
        }
        // CharIsPrintable used to be Char.IsPrintable, but Jay removed it and
        // is forcing people to use the Unicode categories themselves.  Copied
        // the code here.
        private static bool CharIsPrintable(char c)
        {
            UnicodeCategory uc = char.GetUnicodeCategory(c);
            return (!(uc == UnicodeCategory.Control) || (uc == UnicodeCategory.Format) ||
                    (uc == UnicodeCategory.LineSeparator) || (uc == UnicodeCategory.ParagraphSeparator) ||
            (uc == UnicodeCategory.OtherNotAssigned));
        }

        internal static bool ValidLogName(string logName, bool ignoreEmpty)
        {
            // No need to trim here since the next check will verify that there are no spaces.
            // We need to ignore the empty string as an invalid log name sometimes because it can
            // be passed in from our default constructor.
            if (logName.Length == 0 && !ignoreEmpty)
                return false;

            //any space, backslash, asterisk, or question mark is bad
            //any non-printable characters are also bad
            foreach (char c in logName)
                if (!CharIsPrintable(c) || (c == '\\') || (c == '*') || (c == '?'))
                    return false;

            return true;
        }

        public void WriteEntry(string message)
        {
            WriteEntry(message, EventLogEntryType.Information, (short)0, 0, null);
        }

        public static void WriteEntry(string source, string message)
        {
            WriteEntry(source, message, EventLogEntryType.Information, (short)0, 0, null);
        }

        public void WriteEntry(string message, EventLogEntryType type)
        {
            WriteEntry(message, type, (short)0, 0, null);
        }

        public static void WriteEntry(string source, string message, EventLogEntryType type)
        {
            WriteEntry(source, message, type, (short)0, 0, null);
        }

        public void WriteEntry(string message, EventLogEntryType type, int eventID)
        {
            WriteEntry(message, type, eventID, 0, null);
        }

        public static void WriteEntry(string source, string message, EventLogEntryType type, int eventID)
        {
            WriteEntry(source, message, type, eventID, 0, null);
        }

        public void WriteEntry(string message, EventLogEntryType type, int eventID, short category)
        {
            WriteEntry(message, type, eventID, category, null);
        }

        public static void WriteEntry(string source, string message, EventLogEntryType type, int eventID, short category)
        {
            WriteEntry(source, message, type, eventID, category, null);
        }

        public static void WriteEntry(string source, string message, EventLogEntryType type, int eventID, short category, byte[] rawData)
        {
            using (EventLogInternal log = new EventLogInternal(string.Empty, ".", CheckAndNormalizeSourceName(source)))
            {
                log.WriteEntry(message, type, eventID, category, rawData);
            }
        }

        public void WriteEntry(string message, EventLogEntryType type, int eventID, short category, byte[] rawData)
        {
            _underlyingEventLog.WriteEntry(message, type, eventID, category, rawData);
        }

        public void WriteEvent(EventInstance instance, params object[] values)
        {
            WriteEvent(instance, null, values);
        }

        public void WriteEvent(EventInstance instance, byte[] data, params object[] values)
        {
            _underlyingEventLog.WriteEvent(instance, data, values);
        }

        public static void WriteEvent(string source, EventInstance instance, params object[] values)
        {
            using (EventLogInternal log = new EventLogInternal(string.Empty, ".", CheckAndNormalizeSourceName(source)))
            {
                log.WriteEvent(instance, null, values);
            }
        }

        public static void WriteEvent(string source, EventInstance instance, byte[] data, params object[] values)
        {
            using (EventLogInternal log = new EventLogInternal(string.Empty, ".", CheckAndNormalizeSourceName(source)))
            {
                log.WriteEvent(instance, data, values);
            }
        }

        // The EventLog.set_Source used to do some normalization and throw some exceptions.  We mimic that behavior here.
        private static string CheckAndNormalizeSourceName(string source)
        {
            source ??= string.Empty;

            // this 254 limit is the max length of a registry key.
            if (source.Length + EventLogKey.Length > 254)
                throw new ArgumentException(SR.Format(SR.ParameterTooLong, nameof(source), 254 - EventLogKey.Length));

            return source;
        }
    }
}
