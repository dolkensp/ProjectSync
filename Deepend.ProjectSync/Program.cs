using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace Deepend.ProjectSync
{
    class Program
    {
        private enum ReturnCode {
            0 = Success,
            2 = ProjectFileNotFound
        };
        
        static Boolean _running = true;

        static Int32 Main(string[] args)
        {
// #if DEBUG
//             args = new String[] { @"D:\Workspaces\carnival\cruise-personaliser\CruiseControl.Web" };
// #endif
            // Configuration Options
            String[] s_excludeFilter          = (ConfigurationManager.AppSettings["Filter.Exclude"] ?? @"^config\.rb$|^bin\\|^.sass-cache\\|^obj\\|\.orig$|\\_compiler\\|\.bat$|\.user$|\.csproj$|\.zip$|\.bak$|\.mdf$|\.log$|\.exe$").Split('|');
            String[] s_noneFilter             = (ConfigurationManager.AppSettings["Filter.None"]    ?? @"\.scss$|\.pubxml$|\\_GUIDES\\").Split('|');
            String[] s_compileFilter          = (ConfigurationManager.AppSettings["Filter.Compile"] ?? @"\.cs$|\.cshtml$|\.aspx$|\.config$|\.asax$|\.ascx$|\.ashx$").Split('|');
            String[] s_contentFilter          = (ConfigurationManager.AppSettings["Filter.Content"] ?? @"").Split('|');
            String defaultContentType         = (ConfigurationManager.AppSettings["DefaultType"]    ?? "Content");
            Int32 pollingPeriod    = Int32.Parse(ConfigurationManager.AppSettings["PollingPeriod"]  ?? "0");
            Boolean takeBackup   = Boolean.Parse(ConfigurationManager.AppSettings["WriteBackup"]    ?? "true");
            Boolean autoOrganize = Boolean.Parse(ConfigurationManager.AppSettings["AutoOrganize"]   ?? "true");

            // TODO: Better CLI argument processing - is there a nuget package for this, or should I use my old array->ToDictionary method?
            // Commend Line Arguments
            String rootPath = (args.Length == 1 && Directory.Exists(args[0])) ? args[0] : Environment.CurrentDirectory;

            if (!rootPath.EndsWith(@"\")) rootPath = String.Format(@"{0}\", rootPath);
            
            Console.WriteLine("Deepend Project Sync - By Peter Dolkens");
            Console.WriteLine();

            // Parsing config
            Regex[] excludeFilter = s_excludeFilter.Select(x => new Regex(x, RegexOptions.IgnoreCase)).ToArray();
            Regex[] noneFilter    = s_noneFilter.Select(x => new Regex(x, RegexOptions.IgnoreCase)).ToArray();
            Regex[] compileFilter = s_compileFilter.Select(x => new Regex(x, RegexOptions.IgnoreCase)).ToArray();
            Regex[] contentFilter = s_contentFilter.Select(x => new Regex(x, RegexOptions.IgnoreCase)).ToArray();

            DateTime nextPoll;

            Uri rootUri = new Uri(rootPath);

            // Find Project File
            String[] projFiles = Directory.GetFiles(rootPath, "*.csproj", SearchOption.TopDirectoryOnly);

            if (projFiles.Length != 1)
            {
                Console.WriteLine("ProjectSync only supports a single .csproj file per root");
                return (Int32)ReturnCode.ProjectFileNotFound;
            }

            FileStream projectStream;

            while (_running)
            {
                // Run Once
                if (pollingPeriod == 0) _running = false;

                using (projectStream = new FileStream(projFiles[0], FileMode.Open))
                {
                    Boolean modified = false;

                    projectStream.Lock(0, projectStream.Length);
                    nextPoll = DateTime.Now.AddSeconds(pollingPeriod);

                    // Parse XML
                    XmlDocument doc = new XmlDocument();

                    doc.Load(projectStream);

                    XmlNamespaceManager mgr = new XmlNamespaceManager(doc.NameTable);
                    mgr.AddNamespace("x", "http://schemas.microsoft.com/developer/msbuild/2003");
                    
                    XmlNodeList allNodes = doc.SelectNodes("//x:ItemGroup/*", mgr);
                    XmlNodeList referenceNodes = doc.SelectNodes("//x:ItemGroup/Reference", mgr);
                    XmlNodeList noneNodes = doc.SelectNodes("//x:ItemGroup/x:None", mgr);
                    XmlNodeList contentNodes = doc.SelectNodes("//x:ItemGroup/x:Content", mgr);
                    XmlNodeList compileNodes = doc.SelectNodes("//x:ItemGroup/x:Compile", mgr);
                    XmlNodeList itemGroups = doc.SelectNodes("//x:ItemGroup", mgr);

                    // Load Virtual Files
                    List<String> virtualFiles = new List<String>();

                    foreach (XmlNode node in noneNodes)
                        virtualFiles.Add(node.Attributes["Include"].Value);
                    foreach (XmlNode node in contentNodes)
                        virtualFiles.Add(node.Attributes["Include"].Value);
                    foreach (XmlNode node in compileNodes)
                        virtualFiles.Add(node.Attributes["Include"].Value);

                    // Load Physical Files
                    String[] realFiles = Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories).Select(x => x.Replace(rootPath, "")).ToArray();

                    String[] removedFiles = virtualFiles.Except(realFiles).ToArray();

                    realFiles = realFiles.Except(from realFile in realFiles
                                                 from filter in excludeFilter
                                                 where filter.IsMatch(realFile)
                                                 select realFile).ToArray();

                    String[] newFiles = realFiles.Except(virtualFiles).ToArray();

                    // Add Missing References
                    foreach (String file in newFiles)
                    {
                        modified = true;

                        String nodeType = String.Empty;

                        if (nodeType == String.Empty)
                            foreach (Regex filter in compileFilter)
                                if (filter.IsMatch(file))
                                {
                                    nodeType = "Compile";
                                    break;
                                }

                        if (nodeType == String.Empty)
                            foreach (Regex filter in contentFilter)
                                if (filter.IsMatch(file))
                                {
                                    nodeType = "Content";
                                    break;
                                }

                        if (nodeType == String.Empty)
                            foreach (Regex filter in noneFilter)
                                if (filter.IsMatch(file)) {
                                    nodeType = "None";
                                    break;
                                }

                        if (nodeType == String.Empty)
                            nodeType = defaultContentType;

                        Console.WriteLine("Adding {0}: {1}", nodeType, file);

                        XmlNode newNode = doc.CreateNode(XmlNodeType.Element, nodeType, "http://schemas.microsoft.com/developer/msbuild/2003");
                        newNode.Attributes.Append(doc.CreateAttribute("Include"));
                        newNode.Attributes["Include"].Value = file;

                        itemGroups[itemGroups.Count - 1].AppendChild(newNode);
                    }

                    Console.WriteLine();

                    // Remove Extra References
                    foreach (String file in removedFiles)
                    {
                        modified = true;

                        String query = String.Format(@"//x:ItemGroup/*[@Include='{0}']", file);

                        XmlNode remove = doc.SelectSingleNode(query, mgr);

                        Console.WriteLine("Removing {0}: {1}", remove.Name, file);

                        remove.ParentNode.RemoveChild(remove);
                    }

                    if (modified) {
                        
                        if (autoOrganize) {
                            // itemGroups[0] == References
                            // itemGroups[1] == Compile
                            // itemGroups[2] == Content
                            // itemGroups[3] == Other
                            // itemGroups[4] == None
                            if (itemGroups.Count > 5) {
                                // Loop through all XmlNodes and append to itemGroups[3]
                                
                                // Delete all itemGroups beyond itemGroups[4]
                            }
                            else if (itemGroups.Count < 5) {
                                // Append itemGroup nodes to itemGroups[0].Parent
                                
                                // Loop through all XmlNodes and append to itemGroups[3]
                            }
                            else {
                                // Loop through all XmlNodes and append to itemGroups[3]
                            }
                            
                            // Loop through all Reference nodes and append to itemGroups[0]
                            // Loop through all Compile nodes and append to itemGroups[1]
                            // Loop through all Content nodes and append to itemGroups[2]
                            // Loop through all None nodes and append to itemGroups[4]
                        }
                        
                        if (takeBackup)
                        {
                            projectStream.Unlock(0, projectStream.Length);
                            File.Copy(projFiles[0], String.Format("{0}{1:yyyyMMdd-HHmmss}.csproj.bak", rootPath, DateTime.Now));
                        }

                        // Save Changes
                        projectStream.Position = 0;
                        doc.Save(projectStream);
                        projectStream.SetLength(projectStream.Position);
                    }
                    else
                        projectStream.Unlock(0, projectStream.Length);
                }

                Console.WriteLine();

                Thread.CurrentThread.Priority = ThreadPriority.Lowest;

                while (_running && nextPoll > DateTime.Now)
                {
                    if (Console.KeyAvailable)
                        _running = false;

                    Thread.Sleep(1000);
                }

                Thread.CurrentThread.Priority = ThreadPriority.Normal;
            }

            if (pollingPeriod != 0) Console.ReadKey();
            
            return (Int32)ReturnCode.Success;
        }
    }
}
