/*
*  Warewolf - The Easy Service Bus
*  Copyright 2016 by Warewolf Ltd <alpha@warewolf.io>
*  Licensed under GNU Affero General Public License 3.0 or later. 
*  Some rights reserved.
*  Visit our website for more information <http://warewolf.io/>
*  AUTHORS <http://warewolf.io/authors.php> , CONTRIBUTORS <http://warewolf.io/contributors.php>
*  @license GNU Affero General Public License <http://www.gnu.org/licenses/agpl-3.0.html>
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using Dev2.Common;
using Dev2.Common.Common;
using Dev2.Common.Interfaces;
using Dev2.Common.Interfaces.Core.Graph;
using Dev2.Common.Interfaces.Data;
using Dev2.Common.Interfaces.DB;
using Dev2.Data;
using Dev2.Data.Util;
using Dev2.DataList.Contract;
using Dev2.Runtime.Hosting;
using Dev2.Runtime.ServiceModel.Data;
using Unlimited.Framework.Converters.Graph;
using Warewolf.Storage;
using WarewolfParserInterop;
using Dev2.Services.Sql;
using System.Security.Cryptography;

namespace Dev2.Services.Execution
{
    public abstract class ServiceExecutionAbstract<TService, TSource> : IServiceExecution
        where TService : Service, new()
        where TSource : Resource, new()
    {
        // Plugins need to handle formatting inside the RemoteObjectHandler 
        // and NOT here otherwise serialization issues occur!
        public readonly ErrorResultTO ErrorResult;

        /// <summary>
        ///     Construction for ServiceExecution
        /// </summary>
        /// <param name="dataObj">DataObject to execute against</param>
        /// <param name="handlesOutputFormatting">
        ///     Does the ServiceExecution handle its own output formatting i.e. is it formatted
        ///     as part of its execution or must it be formatted before merging into the Datalist
        /// </param>
        /// <param name="requiresFormatting">
        ///     Has the execution been put into a DataList already or must its payload be put into the
        ///     DataList
        /// </param>
        protected ServiceExecutionAbstract(IDSFDataObject dataObj, bool handlesOutputFormatting = true,
            bool requiresFormatting = true)
        {
            ErrorResult = new ErrorResultTO();
            DataObj = dataObj;
            HandlesOutputFormatting = handlesOutputFormatting;
            RequiresFormatting = requiresFormatting;
            if (DataObj.ResourceID != Guid.Empty || !string.IsNullOrEmpty(dataObj.ServiceName))
            {
                CreateService(ResourceCatalog.Instance);
            }
        }

        public bool HandlesOutputFormatting { get; private set; }
        public bool RequiresFormatting { get; set; }
        public TSource Source { get; set; }
        public string InstanceOutputDefintions { get; set; }
        public IDSFDataObject DataObj { get; set; }

        public abstract void BeforeExecution(ErrorResultTO errors);

        public virtual Guid Execute(out ErrorResultTO errors, int update)
        {
            //This execution will throw errors from the constructor
            errors = new ErrorResultTO();
            errors.MergeErrors(ErrorResult);
            ExecuteImpl(out errors,update);
            return DataObj.DataListID;
        }

        public abstract void AfterExecution(ErrorResultTO errors);

        protected void CreateService(ResourceCatalog catalog)
        {
            if (!GetService(catalog)) return;
            GetSource(catalog);
        }

        private void GetSource(ResourceCatalog catalog)
        {
            Source = catalog.GetResource<TSource>(GlobalConstants.ServerWorkspaceID, Service.Source.ResourceID) ??
                     catalog.GetResource<TSource>(GlobalConstants.ServerWorkspaceID, Service.Source.ResourceName);
            if (Source == null)
            {
                ErrorResult.AddError(string.Format("Error retrieving DBSource for resource ID:{0} and Name:{1}",
                    Service.Source.ResourceID, Service.Source.ResourceName));
            }
        }

        public void GetSource(Guid sourceId)
        {
            var catalog = ResourceCatalog.Instance;
            ODBCServer Odbc = new ODBCServer();
            List<TSource> list = new List<TSource>();
            var Dsns = Odbc.GetDSN();
            for (int i = 0; i < Dsns.Count; i++)
            {
                string input = Dsns[i];
                using (MD5 md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.Default.GetBytes(input));
                    Guid result = new Guid(hash);

                    list.Add(

                            new TSource
                            {
                                ResourceName = Dsns[i],
                                ResourceType= ResourceType.DbSource,
                                ResourceID = result

                            }

                  );
                }

            }

            Source = list.Find(S => S.ResourceID == sourceId);
            if(Source == null)
            {
                Source = catalog.GetResource<TSource>(GlobalConstants.ServerWorkspaceID, sourceId);
                if (Source == null)
                {
                    ErrorResult.AddError(string.Format("Error retrieving DBSource for resource ID:{0} and Name:{1}",
                        Service.Source.ResourceID, Service.Source.ResourceName));
                }
            }
          
        }
        protected virtual bool GetService(ResourceCatalog catalog)
        {
            Service = catalog.GetResource<TService>(GlobalConstants.ServerWorkspaceID, DataObj.ResourceID) ??
                      catalog.GetResource<TService>(GlobalConstants.ServerWorkspaceID, DataObj.ServiceName);
            if (Service == null)
            {
                ErrorResult.AddError(string.Format("Error loading resource with ID:{0}", DataObj.ResourceID));
                return false;
            }
            return true;
        }

        protected abstract object ExecuteService(int update, out ErrorResultTO errors, IOutputFormatter formater);

        #region ExecuteImpl

        public TService Service { get; set; }
        public string InstanceInputDefinitions { get; set; }
        public ICollection<IServiceInput> Inputs { get; set; }
        public ICollection<IServiceOutputMapping> Outputs { get; set; }

        protected void ExecuteImpl(out ErrorResultTO errors, int update)
        {
            errors = new ErrorResultTO();

            #region Create OutputFormatter

            // ReSharper disable RedundantAssignment
            IOutputFormatter outputFormatter = null;
            // ReSharper restore RedundantAssignment

            try
            {
                if(!string.IsNullOrEmpty(InstanceOutputDefintions))
                {
                    outputFormatter = GetOutputFormatter(Service);
                }
            }
            catch (Exception)
            {
                if (HandlesOutputFormatting)
                {
                    errors.AddError(
                        string.Format("Output format in service action {0} is invalid. Please edit and remap.",
                            Service.ResourceName));
                    return;
                }
            }

            if (HandlesOutputFormatting && outputFormatter == null && !string.IsNullOrEmpty(InstanceOutputDefintions))
            {
                errors.AddError(string.Format("Output format in service action {0} is invalid.", Service.ResourceName));
                return;
            }

            #endregion

            try
            {
                ErrorResultTO invokeErrors;

                var itrs = new List<IWarewolfIterator>(5);
                IWarewolfListIterator itrCollection = new WarewolfListIterator();
                if(string.IsNullOrEmpty(InstanceInputDefinitions) && string.IsNullOrEmpty(InstanceOutputDefintions))
                {
                    if(Inputs != null && Inputs.Count == 0)
                    {
                        ExecuteService(out invokeErrors, update, outputFormatter);
                        errors.MergeErrors(invokeErrors);
                        return;
                    }
                    else
                    {
                        BuildParameterIterators(update, null, itrCollection, itrs);

                        while(itrCollection.HasMoreData())
                        {
                            ExecuteService(itrCollection, itrs, out invokeErrors, update, outputFormatter);
                            errors.MergeErrors(invokeErrors);
                        }
                        return;
                    }
                }
                ServiceMethod method = Service.Method;
                var inputs = method.Parameters;
                if (inputs.Count == 0)
                {
                    ExecuteService(out invokeErrors, update, outputFormatter);
                    errors.MergeErrors(invokeErrors);
                }
                else
                {
                    BuildParameterIterators(update, inputs, itrCollection, itrs);

                    while (itrCollection.HasMoreData())
                    {
                        ExecuteService(itrCollection, itrs, out invokeErrors, update, outputFormatter);
                        errors.MergeErrors(invokeErrors);
                    }
                }
            }
            finally
            {
                var disposable = Service as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }

                // ensure errors bubble up ;)
                errors.MergeErrors(ErrorResult);
            }
        }

        private void BuildParameterIterators(int update, List<MethodParameter> inputs, IWarewolfListIterator itrCollection, List<IWarewolfIterator> itrs)
        {
            if(string.IsNullOrEmpty(InstanceInputDefinitions))
            {
                if(Inputs != null)
                {
                    foreach (var sai in Inputs)
                    {
                        string val = sai.Name;
                        string toInject = null;

                        if (val != null)
                        {
                            toInject = sai.Value;
                        }
                        else if (!sai.EmptyIsNull)
                        {
                            toInject = "";
                        }
                        var paramIterator = new WarewolfIterator(DataObj.Environment.Eval(toInject, update));
                        itrCollection.AddVariableToIterateOn(paramIterator);
                        itrs.Add(paramIterator);
                    }
                }
                return;
            }
            var inputDefs = DataListFactory.CreateInputParser().Parse(InstanceInputDefinitions);
            foreach(MethodParameter sai in inputs)
            {
                string val = sai.Name;
                string toInject = "NULL";

                if(val != null)
                {
                    var sai1 = sai;
                    var dev2Definitions = inputDefs.Where(definition => definition.Name == sai1.Name);
                    var definitions = dev2Definitions as IDev2Definition[] ?? dev2Definitions.ToArray();
                    if(definitions.Length == 1)
                    {
                        toInject = DataListUtil.IsEvaluated(definitions[0].RawValue) ? DataListUtil.AddBracketsToValueIfNotExist(definitions[0].RawValue) : definitions[0].RawValue;
                    }
                }
                else if(!sai.EmptyToNull)
                {
                    toInject = sai.DefaultValue;
                }
                var paramIterator = new WarewolfIterator(DataObj.Environment.Eval(toInject, update));
                itrCollection.AddVariableToIterateOn(paramIterator);
                itrs.Add(paramIterator);
            }
        }

        #endregion

        #region ExecuteServiceAndMergeResultIntoDataList

        #endregion

        #region ExecuteService

        private void ExecuteService(IWarewolfListIterator itrCollection,
            IEnumerable<IWarewolfIterator> itrs, out ErrorResultTO errors,int update, IOutputFormatter formater = null)
        {
            errors = new ErrorResultTO();
            if (Inputs.Any())
            {
                // Loop iterators 
                int pos = 0;
                foreach (var itr in itrs)
                {
                    string injectVal = itrCollection.FetchNextValue(itr);
                    var param = Inputs.ToList()[pos];


                    param.Value = param.EmptyIsNull &&
                                  (injectVal == null ||
                                   string.Compare(injectVal, string.Empty,
                                       StringComparison.InvariantCultureIgnoreCase) == 0)
                        ? null
                        : injectVal;

                    pos++;
                }
            }

            try
            {
                ErrorResultTO invokeErrors;
                ExecuteService(out invokeErrors, update, formater);
                errors.MergeErrors(invokeErrors);
            }
            catch (Exception ex)
            {
                errors.AddError(string.Format("Service Execution Error: {0}", ex.Message));
            }
        }


        private void ExecuteService(out ErrorResultTO errors,int update, IOutputFormatter formater = null)
        {
            errors = new ErrorResultTO();
            try
            {
                var parameters = new List<MethodParameter>();
                if (Service is WebService)
                {
                    var webService = Service as WebService;
                    if (!String.IsNullOrEmpty(webService.RequestBody))
                    {
                        var methodParameter = new MethodParameter();
                        methodParameter.Name = DataListUtil.RemoveLanguageBrackets(webService.RequestBody);
                        methodParameter.Value = ExecutionEnvironment.WarewolfEvalResultToString(DataObj.Environment.Eval(webService.RequestBody, update));
                        parameters.Add(methodParameter);
                    }
                    if (!String.IsNullOrEmpty(webService.RequestHeaders))
                    {
                        var methodParameter = new MethodParameter();
                        methodParameter.Name = DataListUtil.RemoveLanguageBrackets(webService.RequestHeaders);
                        methodParameter.Value = ExecutionEnvironment.WarewolfEvalResultToString(DataObj.Environment.Eval(webService.RequestHeaders, update));
                        parameters.Add(methodParameter);
                    }
                }
                string result;
                if (parameters.Any())
                {
                    result = ExecuteService(update, out errors, formater).ToString();
                }
                else
                {
                    ErrorResultTO invokeErrors;
                    result = ExecuteService(update, out invokeErrors, formater).ToString();
                    errors.MergeErrors(invokeErrors);
                }
                if (!HandlesOutputFormatting)
                {
                    var formattedPayload = formater != null
                            ? formater.Format(result).ToString()
                            : result;
                    PushXmlIntoEnvironment(formattedPayload, update);
                }
                else
                {
                    PushXmlIntoEnvironment(result, update);
                }
            }
            catch (Exception ex)
            {
                errors.AddError(string.Format("Service Execution Error: {0}", ex.Message));
            }
        }

        #endregion

        #region MergeResultIntoDataList

        public void PushXmlIntoEnvironment(string input,int update)
        {

            if (input != string.Empty)
            {
                try
                {
                    string toLoad = DataListUtil.StripCrap(input); // clean up the rubish ;)
                    XmlDocument xDoc = new XmlDocument();
                    toLoad = string.Format("<Tmp{0}>{1}</Tmp{0}>", Guid.NewGuid().ToString("N"), toLoad);
                    xDoc.LoadXml(toLoad);

                    if (xDoc.DocumentElement != null)
                    {
                        XmlNodeList children = xDoc.DocumentElement.ChildNodes;

                        IDictionary<string, int> indexCache = new Dictionary<string, int>();

                        // BUG 9626 - 2013.06.11 - TWR: refactored for recursion
                        var outputDefs = DataListFactory.CreateOutputParser().Parse(InstanceOutputDefintions);
                        TryConvert(children, outputDefs, indexCache, update);
                    }
                }
                catch (Exception e)
                {
                    Dev2Logger.Error(e.Message, e);
                    // if use passed in empty input they only wanted the shape ;)
                    if (input.Length > 0)
                    {
                    }
                }
            }
        }
        void TryConvert(XmlNodeList children, IList<IDev2Definition> outputDefs, IDictionary<string, int> indexCache, int update,int level = 0)
        {
            // spin through each element in the XML
            foreach (XmlNode c in children)
            {
                if (c.Name != GlobalConstants.NaughtyTextNode)
                {
                    // scalars and recordset fetch
                    if ( level>0)
                    {
                        var c1 = c;
                        var recSetName = outputDefs.Where(definition => definition.RecordSetName == c1.Name);
                        var dev2Definitions = recSetName as IDev2Definition[] ?? recSetName.ToArray();
                        if (dev2Definitions.Length != 0)
                        {
                            // fetch recordset index
                            int fetchIdx;
                            var idx = indexCache.TryGetValue(c.Name, out fetchIdx) ? fetchIdx : 1;
                            // process recordset
                            var nl = c.ChildNodes;
                            foreach (XmlNode subc in nl)
                            {
                                // Extract column being mapped to ;)
                                foreach (var definition in dev2Definitions)
                                {
                                    if (definition.MapsTo == subc.Name || definition.Name == subc.Name)
                                    {
                                        DataObj.Environment.AssignWithFrame(new AssignValue(definition.RawValue, subc.InnerXml), update);
                                    }
                                }

                            }
                            // update this recordset index
                            DataObj.Environment.CommitAssign();
                            indexCache[c.Name] = ++idx;
                        }
                        else
                        {
                            var scalarName = outputDefs.FirstOrDefault(definition => definition.Name == c1.Name);
                            if(scalarName != null)
                            {
                                DataObj.Environment.Assign(DataListUtil.AddBracketsToValueIfNotExist(scalarName.RawValue), UnescapeRawXml( c1.InnerXml), update);
                            }
                        }
                    }
                    else
                    {
                        if (level == 0)
                        {
                            // Only recurse if we're at the first level!!
                            TryConvert(c.ChildNodes, outputDefs, indexCache, update, ++level);
                        }
                    }
                }
            }
        }

        string UnescapeRawXml(string innerXml)
        {
            if(innerXml.StartsWith("&lt;") && innerXml.EndsWith("&gt;"))
            {
                return new StringBuilder(innerXml).Unescape().ToString();
            }
            return innerXml;
        }

        #endregion

        #region GetOutputFormatter

        protected virtual IOutputFormatter GetOutputFormatter(TService service)
        {
            return OutputFormatterFactory.CreateOutputFormatter(service.OutputDescription, "root");
        }

        #endregion
    }
}
