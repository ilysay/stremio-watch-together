global using System;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Globalization;
global using System.IO;
global using System.IO.Compression;
global using System.Linq;
global using System.Net;
global using System.Net.Http;
global using System.Net.NetworkInformation;
global using System.Net.Sockets;
global using System.Net.WebSockets;
global using System.Reflection;
global using System.Runtime.InteropServices;
global using System.Text;
global using System.Text.RegularExpressions;
global using System.Threading;
global using System.Threading.Tasks;
// Champ calculé à ne pas écrire dans les fichiers JSON (attribut différent selon la plateforme).
#if NETFRAMEWORK
global using SkipJsonAttribute = System.Web.Script.Serialization.ScriptIgnoreAttribute;
#else
global using SkipJsonAttribute = System.Text.Json.Serialization.JsonIgnoreAttribute;
#endif
