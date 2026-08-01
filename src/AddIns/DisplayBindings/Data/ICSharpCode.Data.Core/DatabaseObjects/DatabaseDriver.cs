// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

#region Usings

using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using ICSharpCode.Data.Core.DatabaseObjects;
using ICSharpCode.Data.Core.Interfaces;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

#endregion

namespace ICSharpCode.Data.Core.DatabaseObjects
{
    /// <summary>
    /// Holds all available database drivers.
    /// </summary>
    public static class DatabaseDriver
    {
        #region Static fields

        private static List<IDatabaseDriver> _databaseDrivers = null;

        #endregion

        #region Static properties

        /// <summary>
        /// Gets all available database drivers.
        /// </summary>
        public static List<IDatabaseDriver> DatabaseDrivers
        {
            get { return _databaseDrivers; }
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Static constructor which loads all available database drivers.
        /// </summary>
        static DatabaseDriver()
        {
            // Get all assumed plug in assemblies
            _databaseDrivers = new List<IDatabaseDriver>();
            FileInfo fileInfo = new FileInfo(Assembly.GetExecutingAssembly().Location);
            string[] files = Directory.GetFiles(fileInfo.Directory.FullName, "ICSharpCode.Data.*.dll");

            // Iterate through all found files and search for IDatabaseDriver interface
            foreach (string file in files)
            {
                try
                {
                    foreach (string typeName in FindDatabaseDriverTypes(file))
                    {
                        // Loading is deliberately deferred until metadata has identified a
                        // concrete driver. Scanning every candidate must not execute module
                        // initializers or pull its dependency graph into the IDE process.
                        Type loadedType = Assembly.LoadFrom(file).GetType(typeName, throwOnError: true);
                        if (Activator.CreateInstance(loadedType) is IDatabaseDriver driver)
                            _databaseDrivers.Add(driver);
                    }
                }
                catch { }
            }
        }

        static IEnumerable<string> FindDatabaseDriverTypes(string fileName)
        {
            using (var stream = File.OpenRead(fileName))
            using (var peReader = new PEReader(stream))
            {
                if (!peReader.HasMetadata)
                    yield break;

                MetadataReader reader = peReader.GetMetadataReader();
                var provider = new MetadataTypeNameProvider();
                foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
                {
                    TypeDefinition definition = reader.GetTypeDefinition(handle);
                    if ((definition.Attributes & TypeAttributes.Abstract) != 0)
                        continue;
                    if (!IsDatabaseDriver(reader, handle, provider, new HashSet<TypeDefinitionHandle>()))
                        continue;

                    string name = reader.GetString(definition.Name);
                    string ns = reader.GetString(definition.Namespace);
                    yield return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                }
            }
        }

        static bool IsDatabaseDriver(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            MetadataTypeNameProvider provider,
            HashSet<TypeDefinitionHandle> visited)
        {
            if (!visited.Add(handle))
                return false;

            TypeDefinition definition = reader.GetTypeDefinition(handle);
            foreach (InterfaceImplementationHandle implementationHandle in definition.GetInterfaceImplementations())
            {
                EntityHandle interfaceHandle = reader.GetInterfaceImplementation(implementationHandle).Interface;
                if (IsDatabaseDriverTypeName(GetTypeName(reader, interfaceHandle, provider)))
                    return true;
            }

            EntityHandle baseType = definition.BaseType;
            if (baseType.IsNil)
                return false;
            if (IsDatabaseDriverTypeName(GetTypeName(reader, baseType, provider)))
                return true;
            return baseType.Kind == HandleKind.TypeDefinition
                && IsDatabaseDriver(reader, (TypeDefinitionHandle)baseType, provider, visited);
        }

        static bool IsDatabaseDriverTypeName(string name)
        {
            return name == "ICSharpCode.Data.Core.Interfaces.IDatabaseDriver"
                || name == "ICSharpCode.Data.Core.Interfaces.IDatabaseDriver`1"
                || name == "ICSharpCode.Data.Core.DatabaseObjects.DatabaseDriver`1";
        }

        static string GetTypeName(MetadataReader reader, EntityHandle handle, MetadataTypeNameProvider provider)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return provider.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0);
                case HandleKind.TypeReference:
                    return provider.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0);
                case HandleKind.TypeSpecification:
                    return reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                        .DecodeSignature(provider, null);
                default:
                    return string.Empty;
            }
        }

        sealed class MetadataTypeNameProvider : ISignatureTypeProvider<string, object>
        {
            static string GetFullName(MetadataReader reader, StringHandle namespaceHandle, StringHandle nameHandle)
            {
                string name = reader.GetString(nameHandle);
                string ns = reader.GetString(namespaceHandle);
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                TypeDefinition type = reader.GetTypeDefinition(handle);
                return GetFullName(reader, type.Namespace, type.Name);
            }

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                TypeReference type = reader.GetTypeReference(handle);
                return GetFullName(reader, type.Namespace, type.Name);
            }

            public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            {
                return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
            }

            public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) => genericType;
            public string GetArrayType(string elementType, ArrayShape shape) => elementType;
            public string GetSZArrayType(string elementType) => elementType;
            public string GetByReferenceType(string elementType) => elementType;
            public string GetPointerType(string elementType) => elementType;
            public string GetPinnedType(string elementType) => elementType;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
            public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetFunctionPointerType(MethodSignature<string> signature) => string.Empty;
        }

        #endregion
    }
    
    /// <summary>
    /// Description of DatabaseDriver.
    /// </summary>
    public abstract class DatabaseDriver<T> : DatabaseObjectBase, IDatabaseDriver where T : IDatasource
    {
        #region Fields

        private DatabaseObjectsCollection<T> _datasources = new DatabaseObjectsCollection<T>();
        
        #endregion
                
        #region Properties

        /// <summary>
        /// Gets or sets the datasources of this database driver.
        /// </summary>
        public DatabaseObjectsCollection<T> Datasources
        {
            get { return _datasources; }
            protected set
            {
                _datasources = value;
                OnPropertyChanged("Datasources");
                OnPropertyChanged("IDatasources");
            }
        }

        /// <summary>
        /// Gets or sets the datasources of this database driver.
        /// </summary>
        public DatabaseObjectsCollection<IDatasource> IDatasources
        {
            get { return _datasources.Cast<IDatasource>().ToDatabaseObjectsCollection(); }
        }

        /// <summary>
        /// Gets the provider name of this database driver.
        /// </summary>
        public virtual string ProviderName
        {
            get { throw new NotImplementedException(); }
        }

        /// <summary>
        /// Gets the ODBC provider name of this database driver.
        /// </summary>
        public virtual string ODBCProviderName
        {
            get { throw new NotImplementedException(); }
        }
        
        #endregion

        #region Public methods

        /// <summary>
        /// Creates a new datasource for this driver.
        /// </summary>
        /// <param name="datasourceName">Location name or IP address</param>
        /// <returns>New datasource</returns>
        public IDatasource CreateNewIDatasource(string datasourceName)
        {
            return CreateNewDatasource(datasourceName);
        }

        /// <summary>
        /// Creates a new datasource for this driver.
        /// </summary>
        /// <param name="name">Location name or IP address</param>
        /// <returns>New datasource</returns>
        public T CreateNewDatasource(string datasourceName)
        {
            T newDatasource = (T)Activator.CreateInstance(typeof(T), new object[]{ this });
            newDatasource.Name = datasourceName;
            return newDatasource;
        }

        /// <summary>
        /// Adds a new datasource for this driver.
        /// </summary>
        /// <param name="datasourceName">Location name or IP address</param>
        /// <returns>Added new datasource</returns>
        public IDatasource AddNewDatasource(string datasourceName)
        {
            T existingDatasource = Datasources.FirstOrDefault(datasource => datasource.Name.ToUpper() == datasourceName.ToUpper());
            if (existingDatasource != null)
                return existingDatasource;
            
            T newDatasource = CreateNewDatasource(datasourceName);
            _datasources.Add(newDatasource);
            return newDatasource;
        }

        /// <summary>
        /// Remove datasource by its name.
        /// </summary>
        /// <param name="datasourceName">Location name or IP address</param>
        public void RemoveDatasource(string datasourceName)
        {
            T existingDatasource = Datasources.FirstOrDefault(datasource => datasource.Name.ToUpper() == datasourceName.ToUpper());
            if (existingDatasource != null)
                _datasources.Remove(existingDatasource);
        }

        /// <summary>
        /// Searches for datasources and populates the Datasources property.
        /// </summary>
        public virtual void PopulateDatasources()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Searches for databases in all available datasources.
        /// </summary>
        public void PopulateDatabases()
        { 
            if (Datasources == null)
                return;

            foreach (IDatasource datasource in Datasources)
            {
                PopulateDatabases(datasource);
            }
        }

        /// <summary>
        /// Searches for databases in a specific datasource.
        /// </summary>
        /// <param name="datasource">Datasource</param>
        public virtual void PopulateDatabases(IDatasource datasource)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Loads tables of a database.
        /// </summary>
        /// <param name="database">Database</param>
        /// <returns>Collection of ITables</returns>
        public virtual DatabaseObjectsCollection<ITable> LoadTables(IDatabase database)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Loads views of a database.
        /// </summary>
        /// <param name="database">Database</param>
        /// <returns>Collection of IViews</returns>
        public virtual DatabaseObjectsCollection<IView> LoadViews(IDatabase database)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Loads procedures of a database.
        /// </summary>
        /// <param name="database">Database</param>
        /// <returns>Collection of IProcedures</returns>
        public virtual DatabaseObjectsCollection<IProcedure> LoadProcedures(IDatabase database)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
