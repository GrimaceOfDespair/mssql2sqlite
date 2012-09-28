using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using DbAccess;
using log4net;
using log4net.Config;

// Configure LOG4NET Using configuration file.
[assembly: log4net.Config.XmlConfigurator(Watch = true)]

namespace Converter
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(params string[] args)
        {
            BasicConfigurator.Configure();

            if (Configuration.Mode == Configuration.ExportMode.Console)
            {
              using (SystemConsole.Create())
              {
                try
                {
                  Console.WriteLine("Exporting database");

                  RunConversion(string.Join(" ", args));
                }
                catch (Exception e)
                {
                  Console.WriteLine(e);
                }
                finally
                {
                  if (Debugger.IsAttached)
                  {
                    Console.WriteLine("Press any key to close");
                    Console.ReadKey();
                  }
                }
              }
            }
            else
            {
              Application.EnableVisualStyles();
              Application.SetCompatibleTextRenderingDefault(false);
              Application.Run(new MainForm());
            }
        }

      private static void RunConversion(string sqlConnString)
      {
        if (string.IsNullOrEmpty(sqlConnString)) sqlConnString = Configuration.SqlServer;
        var sqlitePath = Configuration.Sqlite;
        var password = Configuration.Password;
        var generateTriggers = Configuration.ExportTriggers;
        var tableRegex = new Regex(Configuration.Tables, RegexOptions.IgnoreCase);
        SqlTableSelectionHandler selectionHandler = allTables =>
                                                      {
                                                        var tables = new List<TableSchema>();
                                                        foreach (var table in allTables)
                                                        {
                                                          if (tableRegex.IsMatch(table.TableName))
                                                          {
                                                            Console.WriteLine("Exporting table " + table.TableName);

                                                            tables.Add(table);
                                                          }
                                                        }
                                                        return tables;
                                                      };

        SqlConversionHandler handler = (done, success, percent, msg) =>
                                         {
                                           Console.WriteLine(percent + "% " + msg + (success ? "" : " - ERROR"));

                                           if (done)
                                           {
                                             Console.WriteLine("Conversion done");
                                           }
                                         };

        FailedViewDefinitionHandler viewFailureHandler = vs =>
                                                           {
                                                             Console.WriteLine("Error on view " + vs.ViewName);

                                                             return null;
                                                           };

        SqlServerToSQLite.ConvertSqlServerToSQLiteDatabase(sqlConnString, sqlitePath, password, handler,
            selectionHandler, viewFailureHandler, generateTriggers, false);
      }
    }
}