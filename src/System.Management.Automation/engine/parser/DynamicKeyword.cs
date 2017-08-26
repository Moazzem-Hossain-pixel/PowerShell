/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System.Linq;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Reflection;
using System.Linq.Expressions;
using System.Collections;

// Keyword and its attributes are in the Automation namespace to make them consistent
// with Cmdlet and its attributes; although Keyword does not currently inherit from
// InternalCommand, it conceivably will when appropriate

namespace System.Management.Automation.Language
{
    #region Keyword Command Class

    /// <summary>
    /// An abstract class to extend in order to write a keyword specification. Give this class the KeywordAttribute
    /// in order to use it as a PowerShell DynamicKeyword specification. Any properties with
    /// the KeywordParameterAttribute will be parameters, while properties with the KeywordPropertyAttribute will
    /// be keyword properties.
    /// </summary>
    public abstract class Keyword
    {
        /// <summary>
        /// Constructs a keyword with null runtime delegates. This is intended to be overridden.
        /// </summary>
        protected Keyword()
        {
        }

        /// <summary>
        /// Specifies the call to run on a DynamicKeyword data object before the AST node
        /// containing that keyword is parsed
        /// </summary>
        public Func<DynamicKeyword, ParseError[]> PreParse { get; set; }

        /// <summary>
        /// Specifies the call to run on a DynamicKeyword statement AST immediately after that AST node
        /// has been parsed
        /// </summary>
        public Func<DynamicKeywordStatementAst, ParseError[]> PostParse { get; set; }

        /// <summary>
        /// Specifies the call to run on a DynamicKeyword statement AST node at semantic check time (to perform
        /// any user-specified semantic checks)
        /// </summary>
        public Func<DynamicKeywordStatementAst, ParseError[]> SemanticCheck { get; set; }
    }

    #endregion /* Keyword Command Class */

    #region Dynamic Keyword Specification Attributes

    /// <summary>
    /// Specifies that a class describes a DynamicKeyword for PowerShell
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class KeywordAttribute : ParsingBaseAttribute
    {
        // Default attribute parameter values

        /// <summary>
        /// Default keyword name mode
        /// </summary>
        public const DynamicKeywordNameMode DefaultNameMode = DynamicKeywordNameMode.NoName;

        /// <summary>
        /// Default keyword body mode
        /// </summary>
        public const DynamicKeywordBodyMode DefaultBodyMode = DynamicKeywordBodyMode.Command;
        
        /// <summary>
        /// Default keyword use mode
        /// </summary>
        public const DynamicKeywordUseMode DefaultUseMode = DynamicKeywordUseMode.OptionalMany;

        /// <summary>
        /// Default keyword resource name -- TODO: This might be better as null
        /// </summary>
        public static readonly string DefaultResourceName = null;

        /// <summary>
        /// Keyword defaults to being a marshalled call
        /// </summary>
        public const bool DefaultIsDirectCall = false;

        /// <summary>
        /// Keyword defaults to being part of the AST
        /// </summary>
        public const bool DefaultIsMetaStatement = false;

        /// <summary>
        /// Construct a KeywordAttribute with default values
        /// </summary>
        public KeywordAttribute()
        {
        }

        /// <summary>
        /// Specifies whether a DynamicKeyword has a name, and if so what kind
        /// </summary>
        public DynamicKeywordNameMode Name { get; set; } = DefaultNameMode;

        /// <summary>
        /// Specifies the body type of the DynamicKeyword
        /// </summary>
        public DynamicKeywordBodyMode Body { get; set; } = DefaultBodyMode;

        /// <summary>
        /// Specifies how many times the keyword may be used
        /// </summary>
        public DynamicKeywordUseMode Use { get; set; } = DefaultUseMode;

        /// <summary>
        /// The DSC resource name of the keyword, if it is a DSC dynamic keyword
        /// </summary>
        public string ResourceName { get; set; } = DefaultResourceName;

        /// <summary>
        /// Indicates whether a keyword uses a marshalled call or is a direct function call, for DSC node keywords
        /// </summary>
        public bool DirectCall { get; set; } = DefaultIsDirectCall;

        /// <summary>
        /// Indicates that the keyword should not be added to the AST (and therefore should do nothing at runtime) if true
        /// </summary>
        public bool MetaStatement { get; set; } = DefaultIsMetaStatement;
    }

    /// <summary>
    /// Attribute defining a class property as a PowerShell dynamic keyword parameter
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class KeywordParameterAttribute : ParsingBaseAttribute
    {
        /// <summary>
        /// Parameters are not mandatory by default
        /// </summary>
        public const bool NotMandatory = false;

        /// <summary>
        /// Construct a KeywordParameterAttribute with default values
        /// </summary>
        public KeywordParameterAttribute()
        {
        }

        /// <summary>
        /// Specifies whether this parameter is mandatory
        /// </summary>
        public bool Mandatory { get; set; } = NotMandatory;
    }

    /// <summary>
    /// Attribute defining a class property as a PowerShell dynamic keyword property (for hashtable keywords)
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class KeywordPropertyAttribute : ParsingBaseAttribute
    {
        /// <summary>
        /// Properties are not mandatory by default
        /// </summary>
        public const bool NotMandatory = false;

        /// <summary>
        /// Construct a KeywordPropertyAttribute with default values
        /// </summary>
        public KeywordPropertyAttribute()
        {
        }

        /// <summary>
        /// Specifies whether this property is mandatory
        /// </summary>
        public bool Mandatory { get; set; } = NotMandatory;
    }

    #endregion

    #region Dynamic Keyword Parser Datastructures

    #region Dynamic Keyword Namespacing

    /// <summary>
    /// Defines a scoped namespace for Dynamic Keywords. This class is intended to both
    /// encapsulate nested dynamic keyword scoping, and be the first step toward making
    /// DynamicKeyword storage parser local instead of thread static
    /// </summary>
    internal class DynamicKeywordScope
    {
        private Dictionary<DynamicKeyword, int> _seenKeywords;
        private Dictionary<string, DynamicKeyword> _scopeKeywords;

        /// <summary>
        /// Construct a keyword scope from a keyword whose scope we are entering
        /// </summary>
        /// <param name="parentScope">the scope enclosing this one</param>
        /// <param name="enclosingKeyword">the keyword that owns this scope</param>
        internal DynamicKeywordScope(DynamicKeywordScope parentScope, DynamicKeyword enclosingKeyword)
            : this(parentScope, enclosingKeyword, enclosingKeyword.InnerKeywords)
        {
        }

        /// <summary>
        /// Construct a global keyword scope -- a top level scope with no enclosing scope or keyword
        /// </summary>
        /// <param name="globalKeywords">a enumerable of keywords to be globally defined</param>
        internal DynamicKeywordScope(IEnumerable<DynamicKeyword> globalKeywords)
            : this(null, null, globalKeywords.ToDictionary(kw => kw.Keyword, kw => kw))
        {
        }

        /// <summary>
        /// A general initialization constructor to be called by other constructors
        /// </summary>
        /// <param name="parentScope">the enclosing scope of this scope, may be null</param>
        /// <param name="enclosingKeyword">the keyword that owns this scope, may be null</param>
        /// <param name="scopeKeywords">the keywords that are keywords in this scope</param>
        private DynamicKeywordScope(DynamicKeywordScope parentScope,
            DynamicKeyword enclosingKeyword,
            Dictionary<string, DynamicKeyword> scopeKeywords)
        {
            ParentScope = parentScope;
            EnclosingKeyword = enclosingKeyword;
            _scopeKeywords = scopeKeywords;
            _seenKeywords = new Dictionary<DynamicKeyword, int>();
        }

        internal DynamicKeywordScope ParentScope { get; }

        internal DynamicKeyword EnclosingKeyword { get; }

        /// <summary>
        /// Find the named dynamic keyword in this scope
        /// </summary>
        /// <param name="name">the name of the dynamic keyword to find</param>
        /// <returns>the named dynamic keyword, or null if no such keyword is defined</returns>
        public DynamicKeyword GetKeyword(string name)
        {
            DynamicKeyword keyword;
            _scopeKeywords.TryGetValue(name, out keyword);
            return keyword;
        }

        /// <summary>
        /// Get a list of all dynamic keywords defined in this scope
        /// </summary>
        /// <returns>a list of all dynamic keywords defined in this scope</returns>
        public List<DynamicKeyword> GetKeyword()
        {
            return new List<DynamicKeyword>(_scopeKeywords.Values);
        }

        /// <summary>
        /// Remove the named keyword from this scope
        /// </summary>
        /// <param name="name">the name of the keyword to remove</param>
        /// <returns>
        /// true if a keyword by the given name existed and was removed, false otherwise
        /// </returns>
        public bool RemoveKeyword(string name)
        {
            if (String.IsNullOrEmpty(name))
            {
                throw new PSArgumentException(nameof(name));
            }

            return _scopeKeywords.Remove(name);
        }

        /// <summary>
        /// Add the provided keyword to this scope
        /// </summary>
        /// <param name="keywordToAdd">the keyword to add to the scope</param>
        /// <returns>true if the keyword clobbered an existing one, false otherwise</returns>
        public bool AddKeyword(DynamicKeyword keywordToAdd)
        {
            if (keywordToAdd == null)
            {
                throw new PSArgumentException(nameof(keywordToAdd));
            }

            string name = keywordToAdd.Keyword;
            if (String.IsNullOrEmpty(name))
            {
                throw new PSArgumentException(nameof(keywordToAdd.Keyword));
            }

            bool result = _scopeKeywords.Remove(name);
            _scopeKeywords.Add(name, keywordToAdd);
            return result;
        }

        /// <summary>
        /// Check whether a keyword by the given name is defined in this scope
        /// </summary>
        /// <param name="name">the name of the keyword to check</param>
        /// <returns>true if the named keyword is defined in this scope, false otherwise</returns>
        public bool ContainsKeyword(string name)
        {
            return _scopeKeywords.ContainsKey(name);
        }

        /// <summary>
        /// Record that we have seen a keyword if we are trying to keep track of its use. Note that
        /// this does not enforce use semantics of higher-scoped keywords for now. (TODO)
        /// </summary>
        /// <param name="keyword">the keyword that we've seen</param>
        /// <returns>true if we should continue, false if we should throw an error about violating the keyword use semantics</returns>
        public bool TryRecordKeywordUse(DynamicKeyword keyword)
        {
            if (_seenKeywords.ContainsKey(keyword))
            {
                _seenKeywords[keyword]++;
            }
            else
            {
                _seenKeywords.Add(keyword, 1);
            }

            switch (keyword.UseMode)
            {
                case DynamicKeywordUseMode.Required:
                case DynamicKeywordUseMode.Optional:
                    return _seenKeywords[keyword] == 1;

                case DynamicKeywordUseMode.RequiredMany:
                case DynamicKeywordUseMode.OptionalMany:
                    return true;

                default:
                    throw new PSArgumentOutOfRangeException(nameof(keyword.UseMode));
            }
        }

        /// <summary>
        /// Get a list of all the keywords we were required to see in this scope block
        /// but did not. Note that "required" only checks keywords at this scope level;
        /// if a keyword from a higher scope is invoked it is not counted. (TODO)
        /// </summary>
        /// <returns>a (possibly empty) enumeration of all keywords we "required" the use of</returns>
        public List<DynamicKeyword> GetUnusedRequiredKeywords()
        {
            var unusedKeywords = new List<DynamicKeyword>();

            foreach (var keyword in _scopeKeywords.Values)
            {
                switch (keyword.UseMode)
                {
                    case DynamicKeywordUseMode.Required:
                    case DynamicKeywordUseMode.RequiredMany:
                        if (!_seenKeywords.ContainsKey(keyword))
                        {
                            unusedKeywords.Add(keyword);
                        }
                        break;
                }
            }

            return unusedKeywords;
        }

        /// <summary>
        /// Wipe this namespace clean. If this is tried while in a scope, we will be in an
        /// invalid state, hence it is disallowed
        /// </summary>
        public void Reset()
        {
            _seenKeywords.Clear();
            _scopeKeywords.Clear();
        }
    }

    /// <summary>
    /// Holds the current context for the dynamic keyword parser, including
    /// all namespaces
    /// </summary>
    internal sealed class DynamicKeywordParseState
    {
        private Stack<DynamicKeywordScope> _scopeStack;

        internal DynamicKeywordParseState()
        {
            CurrentScope = new DynamicKeywordScope(new DynamicKeyword[0]);
            GlobalScope = CurrentScope;
            _scopeStack = new Stack<DynamicKeywordScope>();
        }

        internal DynamicKeywordScope GlobalScope { get; private set; }

        internal DynamicKeywordScope CurrentScope { get; private set; }

        /// <summary>
        /// Enter the scope of a dynamic keyword
        /// </summary>
        /// <param name="invokedKeyword">the keyword we enter the scope of</param>
        public void EnterScope(DynamicKeyword invokedKeyword)
        {
            CurrentScope = new DynamicKeywordScope(CurrentScope, invokedKeyword);
        }

        /// <summary>
        /// Leave the scope we are currently in
        /// </summary>
        public void LeaveScope()
        {
            CurrentScope = CurrentScope.ParentScope;

            if (CurrentScope == null)
            {
                throw new PSInvalidOperationException("Cannot leave the global dynamic keyword scope");
            }
        }

        /// <summary>
        /// Cache the current dynamic keyword namespace and use a fresh one
        /// </summary>
        public void Push()
        {
            _scopeStack.Push(CurrentScope);
            Reset();
        }
        
        /// <summary>
        /// Return to using the last cached dynamic keyword namespace, forgetting the current one
        /// </summary>
        public void Pop()
        {
            CurrentScope = _scopeStack.Pop();
        }

        /// <summary>
        /// Clear out the current scope namespace
        /// </summary>
        public void Reset()
        {
            if (CurrentScope.ParentScope != null)
            {
                throw PSTraceSource.NewInvalidOperationException(
                    "Cannot reset the dynamic keyword parse state when in a local keyword scope");
            }

            CurrentScope = new DynamicKeywordScope(new DynamicKeyword[0]);
            GlobalScope = CurrentScope;
        }

        /// <summary>
        /// Check whether a string defines a dynamic keyword in the current scope or any enclosing scope
        /// </summary>
        /// <param name="name">the name of the keyword to check</param>
        /// <returns>true if the keyword is defined, false otherwise</returns>
        public bool IsDefinedAsKeyword(string name)
        {
            return EnumerateScopes().Any(scope => scope.ContainsKeyword(name));
        }

        /// <summary>
        /// Get the most local definition for a keyword
        /// </summary>
        /// <param name="name">the name of the keyword to look up</param>
        /// <returns>the named keyword, if it exists, otherwise null</returns>
        public DynamicKeyword GetKeyword(string name)
        {
            foreach (DynamicKeywordScope scope in EnumerateScopes())
            {
                if (scope.ContainsKeyword(name))
                {
                    return scope.GetKeyword(name);
                }
            }

            return null;
        }

        private IEnumerable<DynamicKeywordScope> EnumerateScopes()
        {
            DynamicKeywordScope currEnumScope = CurrentScope;
            while (currEnumScope != null)
            {
                yield return currEnumScope;
                currEnumScope = currEnumScope.ParentScope;
            }
            yield break;
        }
    }

    #endregion /* Dynamic Keyword Namespacing */

    #endregion /* Dynamic Keyword Parser Datastructures */

    #region Dynamic Keyword Metadata Reader

    /// <summary>
    /// Class to pass metadata reading errors back to the parser to be reported
    /// </summary>
    internal class ParseErrorContainer
    {
        private string ErrorId { get; }

        /// <summary>
        /// The error message, to be formatted
        /// </summary>
        private string ErrorMessage { get; }

        /// <summary>
        /// Any arguments to the error message
        /// </summary>
        private object[] ErrorMessageArgs { get; }

        /// <summary>
        /// Construct a new ParseError value to pass up
        /// </summary>
        /// <param name="errorId">the ID of the error</param>
        /// <param name="errorExpr">the string expression</param>
        /// <param name="args">any arguments to the string expression</param>
        public ParseErrorContainer(string errorId, string errorExpr, params object[] args)
        {
            ErrorId = errorId;
            ErrorMessage = errorExpr;
            ErrorMessageArgs = args;
        }

        /// <summary>
        /// Take the current parser extent to turn this ParseErrorContainer into a ParseError
        /// for the parser to report
        /// </summary>
        /// <param name="importExtent">the extent of the import statement that imports the module being parsed</param>
        /// <returns>a parser error expression the error to report</returns>
        public ParseError GenerateParseError(IScriptExtent importExtent)
        {
            string msg = String.Format(ErrorMessage, ErrorMessageArgs);
            return new ParseError(importExtent, ErrorId, msg);
        }
    }

    internal class DSLKeywordMetadataReader
    {
        private const string CtorName = ".ctor";

        private readonly PSModuleInfo _moduleInfo;
        private readonly List<ParseErrorContainer> _parseErrors;

        // Type providers to resolve metadata into strings for method/constructor signatures.
        // Strings are used when a user has defined a type, since using Type would require loading the assembly,
        // which is what the metadata reader is trying to avoid
        private readonly ITypeProvider<string, IGenericContext<string>> _namingTypeProvider;
        private IGenericContext<string> _currentNamingGenericContext;

        // Type provider to resolve data from types that are known because they belong to the PowerShell assemblies
        // This never uses the generic context, so we don't bother instantiating it
        private readonly ITypeProvider<Type, IGenericContext<Type>> _knownTypeTypeProvider;

        private MetadataReader _metadataReader;
        private Stack<Dictionary<string, List<string>>> _enumDefStack;
        private Stack<HashSet<string>> _keywordDefinitionStack;

        /// <summary>
        /// Indicates if the load has failed. If so, we want to continue parsing the metadata looking for errors, but
        /// not actually add any more keyword data to the list to return; since writing/compiling/parsing/loading keyword
        /// specifications is a lengthy task for the user, we want to give them as much information about what to fix as possible
        /// before returning. But, we also want to prevent allocating big lists of keywords if we can help it.
        /// </summary>
        private bool _hasFailed;

        /// <summary>
        /// Construct a new DLL reader from a loaded module, whose information is
        /// described by a PSModuleInfo object
        /// </summary>
        /// <param name="moduleInfo">the information object describing the module to read Dynamic Keyword specifications from</param>
        public DSLKeywordMetadataReader(PSModuleInfo moduleInfo)
        {
            _moduleInfo = moduleInfo;
            _parseErrors = new List<ParseErrorContainer>();
            _knownTypeTypeProvider = new AttributeTypeProvider();
            _namingTypeProvider = new StringSignatureProvider();
            _hasFailed = false;
        }

        /// <summary>
        /// Parse all globally defined keywords in the keyword specification by recursive descent
        /// </summary>
        /// <returns>An enumerable of top level keywords that have been parsed</returns>
        public IEnumerable<DynamicKeyword> ReadDslKeywords(out IEnumerable<ParseErrorContainer> errors)
        {
            var globalDynamicKeywords = new List<DynamicKeyword>();

            try
            {
                using (FileStream stream = File.OpenRead(_moduleInfo.Path))
                using (var peReader = new PEReader(stream))
                {
                    if (!peReader.HasMetadata)
                    {
                        return null;
                    }

                    _metadataReader = peReader.GetMetadataReader();
                    _enumDefStack = new Stack<Dictionary<string, List<string>>>();
                    _keywordDefinitionStack = new Stack<HashSet<string>>();

                    // Since module writing/compiling/loading is a long process for the user,
                    // we want to collect as many errors as we can before failing. So this will
                    // keep reading keywords even if we hit errors -- in the event of errors, we'll just not load anything
                    foreach (var globalKeyword in ReadGlobalDynamicKeywords())
                    {
                        if (globalKeyword != null)
                        {
                            globalDynamicKeywords.Add(globalKeyword);
                        }
                    }
                }
            }
            catch (BadImageFormatException)
            {
                // If the DLL is somehow invalid, we just ignore it
                return null;
            }
            finally
            {
                errors = _parseErrors;
            }

            if (_hasFailed || globalDynamicKeywords.Count == 0)
            {
                return null;
            }

            return globalDynamicKeywords;
        }

        /// <summary>
        /// Read in enum type names and values from a list of enum type defintions -- this assumes
        /// <paramref name="enumDefinitions"/> only contains TypeDefinitions representing enum types
        /// </summary>
        /// <param name="enumDefinitions"></param>
        /// <returns></returns>
        private Dictionary<string, List<string>> ReadEnumDefinitions(IEnumerable<TypeDefinition> enumDefinitions)
        {
            var enumDefs = new Dictionary<string, List<string>>();
            foreach (var typeDef in enumDefinitions)
            {
                switch (typeDef.BaseType.Kind)
                {
                    case HandleKind.TypeReference:
                        var baseType = _metadataReader.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
                        var enumFields = new List<string>();
                        foreach (FieldDefinitionHandle enumFieldHandle in typeDef.GetFields())
                        {
                            FieldDefinition field = _metadataReader.GetFieldDefinition(enumFieldHandle);
                            string fieldName = _metadataReader.GetString(field.Name);
                            // Ensure the value we add is not the special enum value
                            if (fieldName != "value__")
                            {
                                enumFields.Add(fieldName);
                            }
                        }
                        enumDefs.Add(_metadataReader.GetString(typeDef.Name), enumFields);
                        break;
                }
            }
            return enumDefs;
        }

        /// <summary>
        /// Read in all keywords defined in the module, by starting with top-level keywords and continuing by recursive descent
        /// </summary>
        /// <returns>an enumeration of all keywords defined in the module</returns>
        private IEnumerable<DynamicKeyword> ReadGlobalDynamicKeywords()
        {
            // Go through the module and find all the top level type definitions
            var topLevelEnums = new List<TypeDefinition>();
            var topLevelClasses = new List<TypeDefinition>();
            foreach (var typeDefHandle in _metadataReader.TypeDefinitions)
            {
                TypeDefinition typeDef = _metadataReader.GetTypeDefinition(typeDefHandle);
                if (typeDef.GetDeclaringType().IsNil)
                {
                    if (IsEnum(typeDef))
                    {
                        topLevelEnums.Add(typeDef);
                        continue;
                    }

                    // Assume other types are keyword definitions for now -- we'll check this below
                    topLevelClasses.Add(typeDef);
                }
            }

            // Get all enums defined at this level in the scope
            _enumDefStack.Push(ReadEnumDefinitions(topLevelEnums));

            // Set up the keyword scoping stack to check for duplicate names
            _keywordDefinitionStack.Push(new HashSet<string>());

            // Read in all the keywords
            foreach (var typeDefHandle in _metadataReader.TypeDefinitions)
            {
                var typeDef = _metadataReader.GetTypeDefinition(typeDefHandle);
                var declaringType = typeDef.GetDeclaringType(); // Make sure this keyword is not nested (declared as an inner class)
                CustomAttribute? keywordAttribute = null;
                if (declaringType.IsNil && IsKeywordSpecification(typeDef, ref keywordAttribute))
                {
                    DynamicKeyword keyword = ReadKeywordSpecification(typeDef, keywordAttribute.Value);
                    if (keyword == null)
                    {
                        continue;
                    }
                    yield return keyword;
                }
            }
        }

        /// <summary>
        /// Read in a DynamicKeyword object from a dll specification, using the type definition and KeywordAttribute metadata on the class
        /// </summary>
        /// <param name="typeDef">the type definition defining the keyword</param>
        /// <param name="keywordAttribute">the attribute decorating the class specifying its properties</param>
        /// <param name="isNested">whether the keyword is nested inside another keyword or not</param>
        /// <returns>A fully-described DynamicKeyword data object</returns>
        private DynamicKeyword ReadKeywordSpecification(TypeDefinition typeDef, CustomAttribute keywordAttribute, bool isNested = false)
        {
            string keywordName = _metadataReader.GetString(typeDef.Name);

            if (!HasZeroArgumentConstructor(typeDef))
            {
                _parseErrors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordMetadataNoZeroArgCtor), ParserStrings.DynamicKeywordMetadataNoZeroArgCtor, keywordName));
                _hasFailed = true;
            }

            // Make sure keywords by the same name are not already defined in enclosing scopes -- C# only prevents direct ancestors
            foreach (var enclosingScope in _keywordDefinitionStack)
            {
                // Note: This could also include `|| DynamicKeyword.Contains(keywordName)`, but this shows errors when reloading a module
                //       For now, a more local keyword will shadow a global one anyway (as you might expect)
                if (enclosingScope.Contains(keywordName))
                {
                    _parseErrors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordMetadataKeywordAlreadyDefinedInScope),
                        ParserStrings.DynamicKeywordMetadataKeywordAlreadyDefinedInScope, keywordName));
                    _hasFailed = true;
                }
            }

            // Now register the keyword name in the scope
            _keywordDefinitionStack.Peek().Add(keywordName);

            // Set the keyword properties -- note the defaults are set here, since reading metadata does not execute default attribute setters
            DynamicKeywordAttributeValueData attributeData = ReadKeywordAttributeParameters(keywordAttribute);

            // In global scope, we don't enforce UseMode -- so throw an error if a global keyword is not OptionalMany so the user knows this doesn't make sense
            if (!isNested && attributeData.UseMode != DynamicKeywordUseMode.OptionalMany)
            {
                _parseErrors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordMetadataGlobalKeywordsMustBeOptionalMany),
                    ParserStrings.DynamicKeywordMetadataGlobalKeywordsMustBeOptionalMany, keywordName, attributeData.UseMode, DynamicKeywordUseMode.OptionalMany));
            }

            // Read in enum definitions in the local scope
            _enumDefStack.Push(ReadEnumDefinitions(typeDef.GetNestedTypes().Select(tdHandle => _metadataReader.GetTypeDefinition(tdHandle)).Where(t => IsEnum(t))));

            // Set the current generic context
            IEnumerable<string> genericTypeParameterNames = typeDef.GetGenericParameters()
                .Select(t => _metadataReader.GetString(_metadataReader.GetGenericParameter(t).Name));

            _currentNamingGenericContext = new NamingGenericTypeContext(genericTypeParameterNames, ImmutableArray<string>.Empty);

            // Read in all parameters and properties
            var keywordParameters = new List<DynamicKeywordParameter>();
            var keywordProperties = new List<DynamicKeywordProperty>();
            foreach (var propertyHandle in typeDef.GetProperties())
            {
                var propertyDef = _metadataReader.GetPropertyDefinition(propertyHandle);
                foreach (var attributeHandle in propertyDef.GetCustomAttributes())
                {
                    var keywordMemberAttribute = _metadataReader.GetCustomAttribute(attributeHandle);

                    if (IsKeywordParameterAttribute(keywordMemberAttribute))
                    {
                        // TODO: Should this apply to ScriptBlock-bodied keywords too?
                        // Hashtable-bodied keyword cannot take parameters
                        if (attributeData.BodyMode != DynamicKeywordBodyMode.Command)
                        {
                            _parseErrors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordMetadataNonCommandKeywordHasParameters),
                                ParserStrings.DynamicKeywordMetadataNonCommandKeywordHasParameters, keywordName));
                            _hasFailed = true;
                        }

                        if (!_hasFailed)
                        {
                            keywordParameters.Add(ReadParameterSpecification(propertyDef, keywordMemberAttribute));
                        }
                    }

                    if (IsKeywordPropertyAttribute(keywordMemberAttribute))
                    {
                        // Only Hashtable-bodied keywords can have properties
                        if (attributeData.BodyMode != DynamicKeywordBodyMode.Hashtable)
                        {
                            _parseErrors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordMetadataNonHashtableKeywordHasProperties),
                                ParserStrings.DynamicKeywordMetadataNonHashtableKeywordHasProperties, keywordName, attributeData.BodyMode));
                            _hasFailed = true;
                        }

                        if (!_hasFailed)
                        {
                            keywordProperties.Add(ReadPropertySpecifiction(propertyDef, keywordMemberAttribute));
                        }
                    }
                }
            }

            // Read in all nested keywords
            _keywordDefinitionStack.Push(new HashSet<string>());
            var innerKeywords = new List<DynamicKeyword>();
            foreach (var innerTypeDefHandle in typeDef.GetNestedTypes())
            {
                var innerTypeDef = _metadataReader.GetTypeDefinition(innerTypeDefHandle);
                CustomAttribute? innerKeywordAttribute = null; 
                if (IsKeywordSpecification(innerTypeDef, ref innerKeywordAttribute))
                {
                    if (attributeData.BodyMode == DynamicKeywordBodyMode.Command)
                    {
                        _parseErrors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordMetadataCommandKeywordHasNestedKeywords),
                            ParserStrings.DynamicKeywordMetadataCommandKeywordHasNestedKeywords, keywordName));
                        _hasFailed = true;
                    }

                    DynamicKeyword innerKeyword = ReadKeywordSpecification(innerTypeDef, innerKeywordAttribute.Value, isNested: true);
                    if (!_hasFailed && innerKeyword != null)
                    {
                        innerKeywords.Add(innerKeyword);
                    }
                }
            }

            // Leave the definition scope of this keyword
            _keywordDefinitionStack.Pop();
            _enumDefStack.Pop();

            if (_hasFailed)
            {
                return null;
            }

            // Finally, construct the keyword -- this is designed so the keyword can be constructed as readonly if the constructor is changed
            var keyword = new DynamicKeyword()
            {
                Keyword = keywordName,
                NameMode = attributeData.NameMode,
                BodyMode = attributeData.BodyMode,
                UseMode = attributeData.UseMode,
                ResourceName = attributeData.ResourceName,
                DirectCall = attributeData.IsDirectCall,
                MetaStatement = attributeData.IsMetaStatement,
                ImplementingModule = _moduleInfo.Name,
                ImplementingModuleVersion = _moduleInfo.Version,
                ImplementingModuleInfo = _moduleInfo,
                IsReservedKeyword = false,
                HasReservedProperties = false,
                IsNested = isNested,
            };
            foreach (var parameter in keywordParameters)
            {
                keyword.Parameters.Add(parameter.Name, parameter);
            }
            foreach (var property in keywordProperties)
            {
                keyword.Properties.Add(property.Name, property);
            }
            foreach (var innerKeyword in innerKeywords)
            {
                keyword.InnerKeywords.Add(innerKeyword.Keyword, innerKeyword);
            }
            return keyword;
        }

        /// <summary>
        /// Read the parameters in a KeywordAttribute declaration, to pass on the the DynamicKeyword object that is created
        /// </summary>
        /// <param name="keywordAttribute">the KeywordAttribute metadata</param>
        private DynamicKeywordAttributeValueData ReadKeywordAttributeParameters(CustomAttribute keywordAttribute)
        {
            DynamicKeywordNameMode nameMode = KeywordAttribute.DefaultNameMode;
            DynamicKeywordBodyMode bodyMode = KeywordAttribute.DefaultBodyMode;
            DynamicKeywordUseMode useMode = KeywordAttribute.DefaultUseMode;
            string resourceName = KeywordAttribute.DefaultResourceName;
            bool isDirectCall = KeywordAttribute.DefaultIsDirectCall;
            bool isMetaStatement = KeywordAttribute.DefaultIsMetaStatement;

            CustomAttributeValue<Type> keywordValue = keywordAttribute.DecodeValue(_knownTypeTypeProvider);

            foreach (var attributeParameter in keywordValue.NamedArguments)
            {
                switch (attributeParameter.Name)
                {
                    case nameof(KeywordAttribute.Name):
                        nameMode = (DynamicKeywordNameMode)attributeParameter.Value;
                        break;

                    case nameof(KeywordAttribute.Body):
                        bodyMode = (DynamicKeywordBodyMode)attributeParameter.Value;
                        break;

                    case nameof(KeywordAttribute.Use):
                        useMode = (DynamicKeywordUseMode)attributeParameter.Value;
                        break;

                    case nameof(KeywordAttribute.ResourceName):
                        resourceName = (string)attributeParameter.Value;
                        break;

                    case nameof(KeywordAttribute.DirectCall):
                        isDirectCall = (bool)attributeParameter.Value;
                        break;

                    case nameof(KeywordAttribute.MetaStatement):
                        isMetaStatement = (bool)attributeParameter.Value;
                        break;
                }
            }

            return new DynamicKeywordAttributeValueData(nameMode, bodyMode, useMode, resourceName, isDirectCall, isMetaStatement);
        }

        /// <summary>
        /// Read in a DynamicKeywordParameter object from dll metadata, using the C# property definition and its parameter attribute
        /// </summary>
        /// <param name="propertyDef">the property metadata being read in as a dynamic keyword parameter</param>
        /// <param name="keywordParameterAttribute">the attribute on the property defining its parameters and declaring it as a parameter</param>
        /// <returns></returns>
        private DynamicKeywordParameter ReadParameterSpecification(PropertyDefinition propertyDef, CustomAttribute keywordParameterAttribute)
        {
            string parameterName = _metadataReader.GetString(propertyDef.Name);
            string parameterType = propertyDef.DecodeSignature(_namingTypeProvider, _currentNamingGenericContext).ReturnType;

            // Read properties set in the attribute
            bool mandatory = KeywordParameterAttribute.NotMandatory;
            CustomAttributeValue<Type> parameterAttribute = keywordParameterAttribute.DecodeValue(_knownTypeTypeProvider);
            foreach (var parameterParameter in parameterAttribute.NamedArguments)
            {
                switch (parameterParameter.Name)
                {
                    case nameof(KeywordParameterAttribute.Mandatory):
                        mandatory = (bool)parameterParameter.Value;
                        break;
                }
            }

            // Metadata types may be qualified with assembly names like "[System.Management.Automation]System.Management.Automation.SwitchParameter"
            // So we try to remove the part in square brackets to align with C# <type>.FullName
            if (parameterType.StartsWith("["))
            {
                int asmEnd = parameterType.IndexOf(']');
                if (asmEnd != -1)
                {
                    parameterType = parameterType.Remove(0, asmEnd + 1);
                }
                else
                {
                    // Something weird has happened, since type names can't have rogue '['s in them...
                    throw PSTraceSource.NewArgumentOutOfRangeException(nameof(parameterType), parameterType);
                }
            }

            var keywordParameter = new DynamicKeywordParameter()
            {
                Name = parameterName,
                TypeConstraint = parameterType,
                Mandatory = mandatory,
                Switch = parameterType == typeof(SwitchParameter).FullName,
            };
            // If the parameter has an enum type, set the values it can take
            TrySetMemberEnumType(keywordParameter);
            return keywordParameter;
        }

        /// <summary>
        /// Read in a DynamicKeywordProperty object from dll metadata, using the C# property definition and its property attribute
        /// </summary>
        /// <param name="propertyDef">the property being read in as a dynamic keyword property</param>
        /// <param name="keywordPropertyAttribute">the attribute on the property defining it as a dynamic keyword property</param>
        /// <returns>a fully formed dynamic keyword property</returns>
        private DynamicKeywordProperty ReadPropertySpecifiction(PropertyDefinition propertyDef, CustomAttribute keywordPropertyAttribute)
        {
            string propertyName = _metadataReader.GetString(propertyDef.Name);
            string propertyType = propertyDef.DecodeSignature(_namingTypeProvider, _currentNamingGenericContext).ReturnType;

            // Read in properties set in the attribute
            bool mandatory = KeywordPropertyAttribute.NotMandatory;
            CustomAttributeValue<Type> propertyAttribute = keywordPropertyAttribute.DecodeValue(_knownTypeTypeProvider);
            foreach (var propertyParameter in propertyAttribute.NamedArguments)
            {
                switch (propertyParameter.Name)
                {
                    case nameof(KeywordPropertyAttribute.Mandatory):
                        mandatory = (bool)propertyParameter.Value;
                        break;
                }
            }

            var keywordProperty = new DynamicKeywordProperty()
            {
                Name = propertyName,
                TypeConstraint = propertyType,
                Mandatory = mandatory,
            };
            // If the property has an enum type, set its possible values
            TrySetMemberEnumType(keywordProperty);
            return keywordProperty;
        }

        /// <summary>
        /// Checks whether a type definition represents a keyword specification, and if so sets the
        /// <paramref name="keywordAttribute"/> parameter to the custom attribute on the type definition
        /// representing the KeywordAttribute
        /// </summary>
        /// <param name="typeDef">the type definition metadata to check</param>
        /// <param name="keywordAttribute">the custom attribute representing the <see cref="KeywordAttribute"/></param>
        /// <returns>
        /// true if the type definition represents a keyword specification (and sets <paramref name="keywordAttribute"/>),
        /// otherwise false (and does not set <paramref name="keywordAttribute"/>)
        /// </returns>
        private bool IsKeywordSpecification(TypeDefinition typeDef, ref CustomAttribute? keywordAttribute)
        {
            // First check the type defininition has the KeywordAttribute
            bool hasKeywordAttribute = false;
            foreach (CustomAttributeHandle attrHandle in typeDef.GetCustomAttributes())
            {
                CustomAttribute customAttribute = _metadataReader.GetCustomAttribute(attrHandle);
                if (IsKeywordAttribute(customAttribute))
                {
                    keywordAttribute = customAttribute;
                    hasKeywordAttribute = true;
                }
            }

            if (!hasKeywordAttribute)
            {
                return false;
            }

            // Now check the type definition inherits from Keyword
            EntityHandle baseTypeHandle = typeDef.BaseType;
            if (!baseTypeHandle.IsNil)
            {
                switch (baseTypeHandle.Kind)
                {
                    case HandleKind.TypeReference:
                        TypeReference typeRef = _metadataReader.GetTypeReference((TypeReferenceHandle)baseTypeHandle);
                        if (_metadataReader.GetString(typeRef.Name) == nameof(Keyword) && _metadataReader.GetString(typeRef.Namespace) == typeof(Keyword).Namespace)
                        {
                            return true;
                        }
                        break;
                }
            }

            // If we've gotten here, the class has the KeywordAttribute but does not inherit from Keyword
            // So the parse has failed and we must tell the user what the problem is
            _hasFailed = true;
            _parseErrors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordMetadataKeywordDoesNotInheritKeyword),
                ParserStrings.DynamicKeywordMetadataKeywordDoesNotInheritKeyword, _metadataReader.GetString(typeDef.Name)));
            return false;
        }

        /// <summary>
        /// Check that the given attribute metadata represents a KeywordAttribute
        /// </summary>
        /// <param name="attribute">the attribute metadata to check</param>
        /// <returns>true if the attribute represents a KeywordAttribute, false otherwise</returns>
        private bool IsKeywordAttribute(CustomAttribute attribute)
        {
            return IsAttributeOfType(attribute, typeof(KeywordAttribute));
        }

        /// <summary>
        /// Check that the given attribute metadata represents a KeywordParameterAttribute
        /// </summary>
        /// <param name="attribute">the attribute metadata to check</param>
        /// <returns>true if the attribute rerpresents a KeywordParameterAttribute, false otherwise</returns>
        private bool IsKeywordParameterAttribute(CustomAttribute attribute)
        {
            return IsAttributeOfType(attribute, typeof(KeywordParameterAttribute));
        }

        /// <summary>
        /// Check that the given attribute metadata represents a KeywordPropertyAttribute
        /// </summary>
        /// <param name="attribute">the attribute metadata to check</param>
        /// <returns>true if the attribute represents a KeywordPropertyAttribute, false otherwise</returns>
        private bool IsKeywordPropertyAttribute(CustomAttribute attribute)
        {
            return IsAttributeOfType(attribute, typeof(KeywordPropertyAttribute));
        }

        /// <summary>
        /// Checks whether a metadata-read custom attribute is of a known type. This will not load the
        /// type of the attribute, but instead checks the name and namespace of the known type against
        /// the metadata of the read type
        /// </summary>
        /// <param name="attribute">the custom attribute whose type to check</param>
        /// <param name="type">the type that the attribute is checked against</param>
        /// <returns>true if the attribute is of the given type, false otherwise</returns>
        private bool IsAttributeOfType(CustomAttribute attribute, Type type)
        {
            switch (attribute.Constructor.Kind)
            {
                case HandleKind.MethodDefinition:
                    // BUG: System.Reflection.Metadata does not present the Parent of a MethodDefinition, so we cannot check.
                    // However, this only applies to attributes defined in the same file -- so we are safe unless there
                    // there is a custom attribute inheriting from one we are looking for. We could seal the Keyword attribute classes to prevent this.
                    return false;

                case HandleKind.MemberReference:
                    MemberReference member = _metadataReader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    StringHandle typeName;
                    StringHandle typeNamespace;
                    switch (member.Parent.Kind)
                    {
                        case HandleKind.TypeReference:
                            TypeReference typeRef = _metadataReader.GetTypeReference((TypeReferenceHandle)member.Parent);
                            typeName = typeRef.Name;
                            typeNamespace = typeRef.Namespace;
                            break;

                        case HandleKind.TypeDefinition:
                            TypeDefinition typeDef = _metadataReader.GetTypeDefinition((TypeDefinitionHandle)member.Parent);
                            typeName = typeDef.Name;
                            typeNamespace = typeDef.Namespace;
                            break;

                        default:
                            return false;
                    }
                    return _metadataReader.GetString(typeName) == type.Name && _metadataReader.GetString(typeNamespace) == type.Namespace;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Check if a given type definition has a zero-argument constructor -- without which we cannot build it
        /// </summary>
        /// <param name="typeDef">the type definition to look for the constructor on</param>
        /// <returns>true if a zero-arg constructor is found, false otherwise</returns>
        private bool HasZeroArgumentConstructor(TypeDefinition typeDef)
        {
            foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
            {
                MethodDefinition methodDef = _metadataReader.GetMethodDefinition(methodHandle);

                if (_metadataReader.GetString(methodDef.Name) != CtorName)
                {
                    continue;
                }

                MethodSignature<string> methodSignature = methodDef.DecodeSignature(_namingTypeProvider, _currentNamingGenericContext);
                if (methodSignature.RequiredParameterCount == 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Look through all the enums defined so far up the scope stack for an enum with the given name, and return
        /// all the values defined by that enum if it exists
        /// </summary>
        /// <param name="enumTypeName">the name of the enum to look for in the dynamic keyword specification</param>
        /// <returns>IEnumerable of enum values, or null if no such enum is found</returns>
        private IEnumerable<string> GetEnumValues(string enumTypeName)
        {
            foreach (var enumScope in _enumDefStack)
            {
                if (enumScope.ContainsKey(enumTypeName))
                {
                    return enumScope[enumTypeName];
                }
            }

            return null;
        }

        /// <summary>
        /// Use the TypeConstraint property on a DynamicKeywordProperty to try and
        /// set the possible values for its type
        /// </summary>
        /// <param name="keywordProperty">the property to set the enumerated types on</param>
        /// <returns>true if the values were successfully set, false otherwise</returns>
        private bool TrySetMemberEnumType(DynamicKeywordProperty keywordProperty)
        {
            if (String.IsNullOrEmpty(keywordProperty.TypeConstraint))
            {
                return false;
            }

            IEnumerable<string> values = GetEnumValues(keywordProperty.TypeConstraint);
            if (values == null)
            {
                return false;
            }

            keywordProperty.Values.AddRange(values);
            return true;
        }

        /// <summary>
        /// Check whether a type definition describes an enum type or not
        /// </summary>
        /// <param name="typeDef">the type definition to check</param>
        /// <returns>true if the type definition inherits from enum, false otherwise</returns>
        private bool IsEnum(TypeDefinition typeDef)
        {
            if (typeDef.BaseType.IsNil)
            {
                return false;
            }

            switch (typeDef.BaseType.Kind)
            {
                case HandleKind.TypeReference:
                    var baseType = _metadataReader.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
                    return String.Join(".", _metadataReader.GetString(baseType.Namespace), _metadataReader.GetString(baseType.Name)) == typeof(System.Enum).FullName;

                default:
                    return false;
            }
        }

        /// <summary>
        /// A value-passing object for seting Dynamic Keyword attributes
        /// </summary>
        private class DynamicKeywordAttributeValueData
        {
            public readonly DynamicKeywordNameMode NameMode;
            public readonly DynamicKeywordBodyMode BodyMode;
            public readonly DynamicKeywordUseMode UseMode;
            public readonly string ResourceName;
            public readonly bool IsDirectCall;
            public readonly bool IsMetaStatement;

            /// <summary>
            /// Create a new object to pass values with
            /// </summary>
            /// <param name="nameMode">the name mode of the dynamic keyword</param>
            /// <param name="bodyMode">the body mode of the dynamic keyword</param>
            /// <param name="useMode">the use mode of the dynamic keyword</param>
            /// <param name="resourceName">the resource name, for a DSC keyword</param>
            /// <param name="isDirectCall">true if the keyword is called directly and not marshalled</param>
            /// <param name="isMetaStatement">true if the keyword does not get added to the AST</param>
            public DynamicKeywordAttributeValueData(DynamicKeywordNameMode nameMode,
                DynamicKeywordBodyMode bodyMode,
                DynamicKeywordUseMode useMode,
                string resourceName,
                bool isDirectCall,
                bool isMetaStatement)
            {
                NameMode = nameMode;
                BodyMode = bodyMode;
                UseMode = useMode;
                ResourceName = resourceName;
                IsDirectCall = isDirectCall;
                IsMetaStatement = isMetaStatement;
            }
        }
    }

    #region Type Providers

    /// <summary>
    /// Provides type representations of metadata types
    /// </summary>
    /// <typeparam name="TType">the type to express type representations with</typeparam>
    /// <typeparam name="TGenericContext">the type that interprets generic types encountered in metadata</typeparam>
    internal interface ITypeProvider<TType, TGenericContext> : ISignatureTypeProvider<TType, TGenericContext>, ICustomAttributeTypeProvider<TType>
        where TGenericContext : IGenericContext<TType>
    {
    }

    /// <summary>
    /// Interprets generic types encountered in metadata
    /// </summary>
    /// <typeparam name="TType">the type with which to represent type metadata interpreted by the context</typeparam>
    internal interface IGenericContext<TType>
    {
        ImmutableArray<TType> TypeParameters { get; }
        ImmutableArray<TType> MethodParameters { get; }
    }

    /// <summary>
    /// A generic context that provides string representations of generic types read in as metadata
    /// </summary>
    internal struct NamingGenericTypeContext : IGenericContext<string>
    {
        private ImmutableArray<string> _typeParameters;
        private ImmutableArray<string> _methodParameters;

        /// <summary>
        /// Construct a new generic context from lists of known generic type parameters and method parameters
        /// </summary>
        /// <param name="typeParameters">generic type parameters defined in this context</param>
        /// <param name="methodParameters">generic method type parameters defined in this context</param>
        public NamingGenericTypeContext(IEnumerable<string> typeParameters, IEnumerable<string> methodParameters)
        {
            _typeParameters = typeParameters.ToImmutableArray();
            _methodParameters = methodParameters.ToImmutableArray();
        }

        /// <summary>
        /// Generic type parameters defined in this context
        /// </summary>
        public ImmutableArray<string> TypeParameters
        {
            get
            {
                return _typeParameters;
            }
        }

        /// <summary>
        /// Generic method type parameters defined in this context
        /// </summary>
        public ImmutableArray<string> MethodParameters
        {
            get
            {
                return _methodParameters;
            }
        }
    }

    /// <summary>
    /// A metadata type interpreter that renders metadata types as strings
    /// </summary>
    internal class StringSignatureProvider : ITypeProvider<string, IGenericContext<string>>
    {
        /// <summary>
        /// Get a string representation of an array of a given type and shape
        /// </summary>
        /// <param name="elementType">the string representation of the base type of the array</param>
        /// <param name="shape">the shape of the array</param>
        /// <returns>a string representing the type of the array</returns>
        public string GetArrayType(string elementType, ArrayShape shape)
        {
            var builder = new StringBuilder();

            builder.Append(elementType);
            builder.Append('[');

            for (int i = 0; i < shape.Rank; i++)
            {
                int lowerBound = 0;

                if (i < shape.LowerBounds.Length)
                {
                    lowerBound = shape.LowerBounds[i];
                    builder.Append(lowerBound);
                }

                builder.Append("...");

                if (i < shape.Sizes.Length)
                {
                    builder.Append(lowerBound + shape.Sizes[i] - 1);
                }

                if (i < shape.Rank - 1)
                {
                    builder.Append(',');
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// Get a string representing a reference to the given type
        /// </summary>
        /// <param name="elementType">the base type of the referenced element</param>
        /// <returns>the string representation of a reference to the element type</returns>
        public string GetByReferenceType(string elementType)
        {
            return elementType + "&";
        }

        /// <summary>
        /// Get a string representing of a function pointer to a method with the given signature
        /// </summary>
        /// <param name="signature">the signature of the method being pointed to</param>
        /// <returns>the string representation of a function pointer to a method of the given signature</returns>
        public string GetFunctionPointerType(MethodSignature<string> signature)
        {
            ImmutableArray<string> parameterTypes = signature.ParameterTypes;

            int requiredParameterCount = signature.RequiredParameterCount;

            var builder = new StringBuilder();
            builder.Append("method ");
            builder.Append(signature.ReturnType);
            builder.Append(" *(");

            int i;
            for (i = 0; i < requiredParameterCount; i++)
            {
                builder.Append(parameterTypes[i]);
                if (i < parameterTypes.Length - 1)
                {
                    builder.Append(", ");
                }
            }

            if (i < parameterTypes.Length)
            {
                builder.Append("..., ");
                for (; i < parameterTypes.Length; i++)
                {
                    builder.Append(parameterTypes[i]);
                    if (i < parameterTypes.Length - 1)
                    {
                        builder.Append(", ");
                    }
                }
            }

            builder.Append(')');
            return builder.ToString();
        }

        /// <summary>
        /// Get a string representing an instantiation of a given generic type
        /// </summary>
        /// <param name="genericType">the representation of the generic type being instantiated</param>
        /// <param name="typeArguments">string representations of the type parameters of the generic type</param>
        /// <returns>the string representing an instantiation of the given generic type with the type arugments given</returns>
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            return genericType + "<" + String.Join(",", typeArguments) + ">";
        }

        /// <summary>
        /// Get a string representing an instantiation of a given generic method type parameter
        /// </summary>
        /// <param name="genericContext">the generic type context providing typing to the method signature type parameters</param>
        /// <param name="index">the index of the type parameter to represent</param>
        /// <returns>the string representation of the method type parameter given by the index</returns>
        public string GetGenericMethodParameter(IGenericContext<string> genericContext, int index)
        {
            return "!!" + genericContext.MethodParameters[index];
        }

        /// <summary>
        /// Get a string representing an instantiation of a given generic type parameter
        /// </summary>
        /// <param name="genericContext">the generic context in which this generic type is being instantiated</param>
        /// <param name="index">the index of the type parameter</param>
        /// <returns>the string representation of the generic type parameter at the given index</returns>
        public string GetGenericTypeParameter(IGenericContext<string> genericContext, int index)
        {
            return "!" + genericContext.TypeParameters[index];
        }

        /// <summary>
        /// Get a string representation of a modified type -- this is highly unlikely to be used
        /// </summary>
        /// <param name="modifier"></param>
        /// <param name="unmodifiedType"></param>
        /// <param name="isRequired"></param>
        /// <returns></returns>
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        {
            return unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";
        }

        /// <summary>
        /// Get a string representation of a pinned type -- this is highly unlikely to be used
        /// </summary>
        /// <param name="elementType"></param>
        /// <returns></returns>

        public string GetPinnedType(string elementType)
        {
            return elementType + " pinned";
        }

        /// <summary>
        /// Get a string representing a pointer type
        /// </summary>
        /// <param name="elementType">string representing the base type being pointed to</param>
        /// <returns>the string representation of a pointer to the given type</returns>
        public string GetPointerType(string elementType)
        {
            return elementType + "*";
        }

        /// <summary>
        /// Get a string representing a primitive dotnet type
        /// </summary>
        /// <param name="typeCode">the metadata type code representing the given primitive type</param>
        /// <returns>the short form C# string representation of the given primitive type</returns>
        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            switch (typeCode)
            {
                case PrimitiveTypeCode.Boolean:
                    return "bool";

                case PrimitiveTypeCode.Byte:
                    return "byte";

                case PrimitiveTypeCode.Char:
                    return "char";

                case PrimitiveTypeCode.Double:
                    return "double";

                case PrimitiveTypeCode.Int16:
                    return "short";

                case PrimitiveTypeCode.Int32:
                    return "int";

                case PrimitiveTypeCode.Int64:
                    return "long";

                case PrimitiveTypeCode.IntPtr:
                    return typeof(IntPtr).ToString();

                case PrimitiveTypeCode.Object:
                    return "object";

                case PrimitiveTypeCode.SByte:
                    return "sbyte";

                case PrimitiveTypeCode.Single:
                    return "float";

                case PrimitiveTypeCode.String:
                    return "string";

                case PrimitiveTypeCode.TypedReference:
                    throw new NotImplementedException("dotnet core does not implement TypedReference");

                case PrimitiveTypeCode.UInt16:
                    return "ushort";

                case PrimitiveTypeCode.UInt32:
                    return "uint";

                case PrimitiveTypeCode.UInt64:
                    return "ulong";

                case PrimitiveTypeCode.UIntPtr:
                    return typeof(UIntPtr).ToString();

                case PrimitiveTypeCode.Void:
                    return "void";

                default:
                    throw new ArgumentOutOfRangeException("Unrecognized primitive type");
            }
        }

        /// <summary>
        /// Get a string reprenting "System.Type"
        /// </summary>
        /// <returns>"System.Type"</returns>
        public string GetSystemType()
        {
            return typeof(System.Type).ToString();
        }

        /// <summary>
        /// Get a string representing a single-dimensional, zero-based array of the given element type
        /// </summary>
        /// <param name="elementType">the base type of elements in the array</param>
        /// <returns>the string representing an SZArray of elements of type <paramref name="elementType"/></returns>
        public string GetSZArrayType(string elementType)
        {
            return elementType + "[]";
        }

        /// <summary>
        /// Get a string representing a type read in from a type definition
        /// </summary>
        /// <param name="reader">the metadata reader containing the type definition data</param>
        /// <param name="handle">the handle to the given type definition</param>
        /// <param name="rawTypeKind"></param>
        /// <returns>the string representation of a type definition</returns>
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind = 0)
        {
            TypeDefinition typeDef = reader.GetTypeDefinition(handle);

            string name = typeDef.Namespace.IsNil
                ? reader.GetString(typeDef.Name)
                : reader.GetString(typeDef.Namespace) + "." + reader.GetString(typeDef.Name);

            // Test if the typedef is nested -- future implementations of System.Reflection.Metadata have typeDef.Attributes.IsNested()
            if (typeDef.Attributes.HasFlag((System.Reflection.TypeAttributes)0x6))
            {
                TypeDefinitionHandle declaringTypeHandle = typeDef.GetDeclaringType();
                return GetTypeFromDefinition(reader, declaringTypeHandle) + "/" + name;
            }

            return name;
        }

        /// <summary>
        /// Get a string representing a type reference
        /// </summary>
        /// <param name="reader">the metadata reader containing the type reference data</param>
        /// <param name="handle">the handle to the given type reference</param>
        /// <param name="rawTypeKind"></param>
        /// <returns>the string representation of the given type reference</returns>
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind = 0)
        {
            TypeReference reference = reader.GetTypeReference(handle);
            Handle scope = reference.ResolutionScope;

            string name = reference.Namespace.IsNil
                ? reader.GetString(reference.Name)
                : reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name);

            switch (scope.Kind)
            {
                case HandleKind.ModuleReference:
                    return "[.module" + reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)scope).Name) + "]" + name;

                case HandleKind.AssemblyReference:
                    var assemblyReference = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                    return "[" + reader.GetString(assemblyReference.Name) + "]" + name;

                case HandleKind.TypeReference:
                    return GetTypeFromReference(reader, (TypeReferenceHandle)scope) + "/" + name;

                default:
                    if (scope == Handle.ModuleDefinition || scope.IsNil)
                    {
                        return name;
                    }
                    throw new ArgumentOutOfRangeException("Unrecognized type handle scope reference");
            }
        }

        /// <summary>
        /// Get a string representing a serialized name
        /// </summary>
        /// <param name="name">the serialized name</param>
        /// <returns>the serialized name</returns>
        public string GetTypeFromSerializedName(string name)
        {
            return name;
        }

        /// <summary>
        /// Get a string representing a type specification
        /// </summary>
        /// <param name="reader">the metadata reader containing the type specification data</param>
        /// <param name="genericContext">the generic context in which the type specification occurs, providing generic type resolution</param>
        /// <param name="handle">a handle to the given type specification</param>
        /// <param name="rawTypeKind"></param>
        /// <returns>the string representation of the given type specification</returns>
        public string GetTypeFromSpecification(MetadataReader reader, IGenericContext<string> genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }

        /// <summary>
        /// Get the underlying primitive type of an enum given its type name.
        /// Since this relies on us knowing about the enum in advance, it is only valid for enum types known here
        /// </summary>
        /// <param name="type">the name of the enum</param>
        /// <returns>the underlying primitive type of the enum</returns>
        public PrimitiveTypeCode GetUnderlyingEnumType(string type)
        {
            string typeName = type.Replace('/', '+');

            if (typeName == nameof(DynamicKeywordNameMode) || typeName == nameof(DynamicKeywordBodyMode) || typeName == nameof(DynamicKeywordUseMode))
            {
                return PrimitiveTypeCode.Int32;
            }

            throw new ArgumentOutOfRangeException("Unrecognized enumerated type");
        }

        /// <summary>
        /// Check whether a string represents "System.Type"
        /// </summary>
        /// <param name="type">the type name to check</param>
        /// <returns>true if <paramref name="type"/>represents <see cref="System.Type"/>, false otherwise</returns>
        public bool IsSystemType(string type)
        {
            return type == "[System.Runtime]System.Type" || Type.GetType(type) == typeof(Type);
        }
    }

    /// <summary>
    /// Type provider to translate the MetadataReader's decoded type into a Type. This is only implemented to the extent needed
    /// for resolving known parameter types such as <see cref="KeywordPropertyAttribute"/> and <see cref="DynamicKeywordBodyMode"/>;
    /// it is likely erroneous for more sophisticated types, especially those not from C#-compiled DLLs, so should not be used outside of
    /// this current restrictive usage
    /// </summary>
    internal class AttributeTypeProvider : ITypeProvider<Type, IGenericContext<Type>>
    {
        /// <summary>
        /// Gets type representation of an array based on its element type -- this throws an exception since we are not checking for known array types
        /// </summary>
        /// <param name="elementType"></param>
        /// <param name="shape"></param>
        /// <returns></returns>
        public Type GetArrayType(Type elementType, ArrayShape shape)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Get a type based on a reference to that type
        /// </summary>
        /// <param name="elementType"></param>
        /// <returns></returns>
        public Type GetByReferenceType(Type elementType)
        {
            return elementType;
        }

        /// <summary>
        /// Get the type of a function pointer based on a typed methodsignature -- this throws an exception since we are not looking for known method signature types
        /// </summary>
        /// <param name="signature"></param>
        /// <returns></returns>
        public Type GetFunctionPointerType(MethodSignature<Type> signature)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Get the type of a generic instantiation of a given type
        /// </summary>
        /// <param name="genericType"></param>
        /// <param name="typeArguments"></param>
        /// <returns></returns>
        public Type GetGenericInstantiation(Type genericType, ImmutableArray<Type> typeArguments)
        {
            string typeName = genericType.ToString() + "<" + String.Join(",", typeArguments.Select(t => t.ToString())) + ">";

            return Type.GetType(typeName);
        }

        /// <summary>
        /// Get the instantiated type of a generic method parameter
        /// </summary>
        /// <param name="genericContext"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public Type GetGenericMethodParameter(IGenericContext<Type> genericContext, int index)
        {
            return genericContext.MethodParameters[index];
        }

        /// <summary>
        /// Get the type of a type parameter in a generic type instantiation
        /// </summary>
        /// <param name="genericContext"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public Type GetGenericTypeParameter(IGenericContext<Type> genericContext, int index)
        {
            return genericContext.TypeParameters[index];
        }

        /// <summary>
        /// Get the type of a modified type -- this may not do what it promises, but we do not expect modified types
        /// </summary>
        /// <param name="modifier"></param>
        /// <param name="unmodifiedType"></param>
        /// <param name="isRequired"></param>
        /// <returns></returns>
        public Type GetModifiedType(Type modifier, Type unmodifiedType, bool isRequired)
        {
            return unmodifiedType;
        }

        /// <summary>
        /// Get the type of a pinned type -- we do not expect to see pinned types
        /// </summary>
        /// <param name="elementType"></param>
        /// <returns></returns>
        public Type GetPinnedType(Type elementType)
        {
            return elementType;
        }

        /// <summary>
        /// Get the type of a pointer to a type -- we do not expect pointer types
        /// </summary>
        /// <param name="elementType"></param>
        /// <returns></returns>
        public Type GetPointerType(Type elementType)
        {
            return elementType;
        }

        /// <summary>
        /// Get the Type representation corresponding to a primitive type code.
        /// TypedReferences are not supported in dotnetCore and will fail
        /// </summary>
        /// <param name="typeCode">the cil metadata type code of the value</param>
        /// <returns>a C# type corresponding to the given type code</returns>
        public Type GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            switch (typeCode)
            {
                case PrimitiveTypeCode.Boolean:
                    return typeof(bool);

                case PrimitiveTypeCode.Byte:
                    return typeof(byte);

                case PrimitiveTypeCode.Char:
                    return typeof(char);

                case PrimitiveTypeCode.Double:
                    return typeof(double);

                case PrimitiveTypeCode.Int16:
                    return typeof(short);

                case PrimitiveTypeCode.Int32:
                    return typeof(int);

                case PrimitiveTypeCode.Int64:
                    return typeof(long);

                case PrimitiveTypeCode.IntPtr:
                    return typeof(IntPtr);

                case PrimitiveTypeCode.Object:
                    return typeof(object);

                case PrimitiveTypeCode.SByte:
                    return typeof(sbyte);

                case PrimitiveTypeCode.Single:
                    return typeof(float);

                case PrimitiveTypeCode.String:
                    return typeof(string);

                case PrimitiveTypeCode.TypedReference:
                    throw new NotImplementedException("TypedReference not supported in dotnetCore");

                case PrimitiveTypeCode.UInt16:
                    return typeof(ushort);

                case PrimitiveTypeCode.UInt32:
                    return typeof(uint);

                case PrimitiveTypeCode.UInt64:
                    return typeof(ulong);

                case PrimitiveTypeCode.UIntPtr:
                    return typeof(UIntPtr);

                case PrimitiveTypeCode.Void:
                    return typeof(void);

                default:
                    throw new ArgumentOutOfRangeException("Unrecognized primitive type: " + typeCode.ToString());
            }
        }

        /// <summary>
        /// Get the Type representation of System.Type
        /// </summary>
        /// <returns></returns>
        public Type GetSystemType()
        {
            return typeof(Type);
        }

        /// <summary>
        /// Get the type representation of a simple array of a given type
        /// </summary>
        /// <param name="elementType"></param>
        /// <returns></returns>
        public Type GetSZArrayType(Type elementType)
        {
            return Type.GetType(elementType.ToString() + "[]");
        }

        /// <summary>
        /// Get the type of a type definition
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="handle"></param>
        /// <param name="rawTypeKind"></param>
        /// <returns></returns>
        public Type GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind=0)
        {
            TypeDefinition typeDef = reader.GetTypeDefinition(handle);

            string typeDefName = reader.GetString(typeDef.Name);

            // Check if type definition is nested
            // This will be typeDef.Attributes.IsNested() in later releases
            if (typeDef.Attributes.HasFlag((System.Reflection.TypeAttributes)0x00000006))
            {
                TypeDefinitionHandle declaringTypeHandle = typeDef.GetDeclaringType();
                Type enclosingType = GetTypeFromDefinition(reader, declaringTypeHandle);
                return Type.GetType(Assembly.CreateQualifiedName(enclosingType.AssemblyQualifiedName, enclosingType.ToString() + "+" + typeDefName));
            }

            string typeDefNamespace = reader.GetString(typeDef.Namespace);
            return Type.GetType(Assembly.CreateQualifiedName(typeDefNamespace, typeDefName));
        }

        /// <summary>
        /// Get the type of a type reference
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="handle"></param>
        /// <param name="rawTypeKind"></param>
        /// <returns></returns>
        public Type GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind=0)
        {
            TypeReference typeRef = reader.GetTypeReference(handle);
            string typeRefName = reader.GetString(typeRef.Name);
            if (typeRef.Namespace.IsNil)
            {
                return Type.GetType(typeRefName);
            }
            string typeRefNamespace = reader.GetString(typeRef.Namespace);
            return Type.GetType(Assembly.CreateQualifiedName(typeRefNamespace, typeRefName));
        }

        /// <summary>
        /// Get the type of a serialized type name
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public Type GetTypeFromSerializedName(string name)
        {
            return Type.GetType(name);
        }

        /// <summary>
        /// Get the type of a type specification
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="genericContext"></param>
        /// <param name="handle"></param>
        /// <param name="rawTypeKind"></param>
        /// <returns></returns>
        public Type GetTypeFromSpecification(MetadataReader reader, IGenericContext<Type> genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Get the underlying type of an enum type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public PrimitiveTypeCode GetUnderlyingEnumType(Type type)
        {
            if (type.Name == nameof(DynamicKeywordNameMode) || type.Name == nameof(DynamicKeywordBodyMode) || type.Name == nameof(DynamicKeywordUseMode))
            {
                return PrimitiveTypeCode.Int32;
            }

            throw new ArgumentOutOfRangeException("Not a known enum type");
        }

        /// <summary>
        /// Check whether a type is <see cref="System.Type"/>
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public bool IsSystemType(Type type)
        {
            return type == typeof(Type);
        }
    }

    #endregion /* Type Providers */

    #endregion /* Dynamic Keyword Metadata Reader */

    #region Dynamic Keyword Reflection Type Loader

    /// <summary>
    /// Loads runtime information from DynamicKeywords
    /// </summary>
    internal class DynamicKeywordLoader
    {
        // Global keywords to be loaded in (which may contain inner keywords to be loaded)
        private readonly ImmutableList<DynamicKeyword> _topLevelKeywordsToLoad;
        // Errors encountered while loading
        private readonly List<ParseErrorContainer> _errors;

        // Dictionary to record where a given keyword should be loaded from
        private IImmutableDictionary<DynamicKeyword, Assembly> _keywordAssemblies;

        /// <summary>
        /// If the load fails for some reason, we still want to keep scanning the data to give
        /// users helpful error messages, but we also don't want to load invalid keywords. This
        /// flag indicates that we should keep reading data and collecting errors, but not do
        /// anything else
        /// </summary>
        private bool _hasFailed;

        /// <summary>
        /// Construct a fresh DynamicKeywordLoader around a list of keywords to load
        /// </summary>
        /// <param name="keywordsToLoad"></param>
        public DynamicKeywordLoader(IEnumerable<DynamicKeyword> keywordsToLoad)
        {
            _errors = new List<ParseErrorContainer>();
            _topLevelKeywordsToLoad = keywordsToLoad.ToImmutableList();
            _hasFailed = false;
        }

        /// <summary>
        /// Perform the loading process
        /// </summary>
        public void Load(out IEnumerable<ParseErrorContainer> errors)
        {
            // Make sure we do no loading if there are no keywords to load
            if (!_topLevelKeywordsToLoad.Any())
            {
                errors = _errors;
                return;
            }

            ValidateKeywords();

            if (!_errors.Any())
            {
                LoadModules();
            }

            if (!_errors.Any())
            {
                LoadKeywords();
            }

            errors = _errors;
        }

        /// <summary>
        /// Load the modules in, being careful not to load any twice and recording which keyword belongs to which module.
        /// Generally, there will only be one module -- but we want to be flexible
        /// </summary>
        private void LoadModules()
        {
            var loadedModules = new Dictionary<PSModuleInfo, Assembly>();
            var keywordAssemblies = new Dictionary<DynamicKeyword, Assembly>();
            // Go through the keywords we want to load and load their respective modules
            foreach (var keyword in _topLevelKeywordsToLoad)
            {
                if (!loadedModules.ContainsKey(keyword.ImplementingModuleInfo))
                {
                    loadedModules.Add(keyword.ImplementingModuleInfo, Assembly.LoadFrom(keyword.ImplementingModuleInfo.Path));
                }

                keywordAssemblies.Add(keyword, loadedModules[keyword.ImplementingModuleInfo]);
            }
            // Remember the assemblies we loaded so we can find types later
            _keywordAssemblies = keywordAssemblies.ToImmutableDictionary();
        }

        /// <summary>
        /// Once the assemblies are loaded, goes through the keywords and checks we can construct their defining commands.
        /// Then, when all keywords have been checked, constructs the keywords
        /// </summary>
        private void LoadKeywords()
        {
            var loadPairs = new List<KeyValuePair<DynamicKeyword, Keyword>>();

            // Go through all the keywords now the types are loaded and prepare the keyword for loading
            foreach (var keyword in _topLevelKeywordsToLoad)
            {
                PrepareKeywordLoad(keyword, loadPairs);
            }

            // Now everything is checked, we can load all the keywords without worrying about partial state mutation
            foreach (var loadPair in loadPairs)
            {
                DynamicKeyword keyword = loadPair.Key;
                keyword.PreParse = loadPair.Value.PreParse;
                keyword.PostParse = loadPair.Value.PostParse;
                keyword.SemanticCheck = loadPair.Value.SemanticCheck;
            }
        }

        /// <summary>
        /// Check there is a type in the module with the same name as the keyword, and that it is of type <see cref="Keyword"/>,
        /// then add the <see cref="DynamicKeyword"/>/<see cref="Keyword"/> pair to the list for delegate loading
        /// </summary>
        /// <param name="keyword">the dynamic keyword to prepare for loading</param>
        /// <param name="loadPairs">the list of dynamic keyword/keyword instance pairs to add to</param>
        private void PrepareKeywordLoad(DynamicKeyword keyword, List<KeyValuePair<DynamicKeyword, Keyword>> loadPairs)
        {
            // Make sure there is a keyword instance type defined as we expect
            Type definingType = _keywordAssemblies[keyword].GetType(keyword.Keyword);
            if (definingType == null)
            {
                _errors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordLoaderKeywordNotFound),
                    ParserStrings.DynamicKeywordLoaderKeywordNotFound, keyword, _keywordAssemblies[keyword].Location));
                _hasFailed = true;
            }

            // Now check the keyword instance type actually inherits from Keyword
            Keyword definitionInstance = Activator.CreateInstance(definingType) as Keyword;
            if (definitionInstance == null)
            {
                _errors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordLoaderKeywordDoesNotInheritFromKeyword),
                    ParserStrings.DynamicKeywordLoaderKeywordDoesNotInheritFromKeyword, keyword));
                _hasFailed = true;
            }

            // Finally, add the pair for loading
            if (!_hasFailed)
            {
                loadPairs.Add(new KeyValuePair<DynamicKeyword, Keyword>(keyword, definitionInstance));
            }

            // Now recurse to child keywords
            foreach (var innerKeyword in keyword.InnerKeywords.Values)
            {
                PrepareNestedKeywordLoad(innerKeyword, _keywordAssemblies[keyword], definingType, loadPairs);
            }
        }

        /// <summary>
        /// Perform the keyword load preparations as in <see cref="PrepareKeywordLoad(DynamicKeyword, List{KeyValuePair{DynamicKeyword, Keyword}})"/>,
        /// but with the assumption that a nested keyword will be defined in the same assembly, and under the parent type
        /// </summary>
        /// <param name="keyword">the keyword we want to load (the child keyword)</param>
        /// <param name="definingAssembly">the assembly defining the keyword (only used for error message data)</param>
        /// <param name="parentType">the keyword under which this keyword is defined</param>
        /// <param name="loadPairs">the list of keyword data/instance pairs to load</param>
        private void PrepareNestedKeywordLoad(DynamicKeyword keyword, Assembly definingAssembly, Type parentType, List<KeyValuePair<DynamicKeyword, Keyword>> loadPairs)
        {
            Type childType = parentType.GetNestedType(keyword.Keyword, BindingFlags.Public | BindingFlags.NonPublic);
            if (childType == null)
            {
                _errors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordLoaderKeywordNotFound),
                    ParserStrings.DynamicKeywordLoaderKeywordNotFound, keyword, definingAssembly.Location));
                _hasFailed = true;
            }

            Keyword definitionInstance = Activator.CreateInstance(childType) as Keyword;
            if (definitionInstance == null)
            {
                _errors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordLoaderKeywordDoesNotInheritFromKeyword),
                    ParserStrings.DynamicKeywordLoaderKeywordDoesNotInheritFromKeyword, keyword));
                _hasFailed = true;
            }

            if (!_hasFailed)
            {
                loadPairs.Add(new KeyValuePair<DynamicKeyword, Keyword>(keyword, definitionInstance));
            }

            foreach (var innerKeyword in keyword.InnerKeywords.Values)
            {
                PrepareNestedKeywordLoad(innerKeyword, definingAssembly, childType, loadPairs);
            }
        }

        /// <summary>
        /// Perform validation on keywords before loading any assemblies so that we can try to ensure everything will work
        /// before doing something irreversible. This employs a number of closures.
        /// </summary>
        private void ValidateKeywords()
        {
            // Check that all keywords are defined in the same module as their parents
            PSModuleInfo topLevelKeywordModule = null;
            DynamicKeyword parent = null;
            Action<DynamicKeyword> moduleInfoChecker = keyword =>
            {
                if (keyword.ImplementingModuleInfo != topLevelKeywordModule)
                {
                    _errors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordLoaderChildKeywordFromDifferentModule),
                        ParserStrings.DynamicKeywordLoaderChildKeywordFromDifferentModule, keyword, parent));
                }
            };

            // Check that there are no cycles in the DynamicKeyword definition tree -- this may be an expensive check, and unlikely to occur
            var seenKeywords = new HashSet<DynamicKeyword>();
            Action<DynamicKeyword> cycleChecker = keyword =>
            {
                if (seenKeywords.Contains(keyword))
                {
                    _errors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordLoaderInnerKeywordCycle),
                        ParserStrings.DynamicKeywordLoaderInnerKeywordCycle, keyword));
                }
                seenKeywords.Add(keyword);
            };

            foreach (var keyword in _topLevelKeywordsToLoad)
            {
                // Also make sure the parent keywords have a module
                if (keyword.ImplementingModuleInfo == null)
                {
                    _errors.Add(new ParseErrorContainer(nameof(ParserStrings.DynamicKeywordLoaderKeywordModuleIsNull),
                        ParserStrings.DynamicKeywordLoaderKeywordModuleIsNull, keyword));
                }

                // Perform the checks
                topLevelKeywordModule = keyword.ImplementingModuleInfo;
                parent = keyword;
                VisitKeywordChildren(keyword, new[] { moduleInfoChecker, cycleChecker });
                seenKeywords = new HashSet<DynamicKeyword>();
            }
        }

        /// <summary>
        /// Visit a keyword and all its children with a list of delegates, executed in enumeration order
        /// </summary>
        /// <param name="keyword">the top keyword to visit</param>
        /// <param name="visitors">the action delegates to execute on the keywords</param>
        private void VisitKeywordChildren(DynamicKeyword keyword, IEnumerable<Action<DynamicKeyword>> visitors)
        {
            foreach (var visitor in visitors)
            {
                visitor(keyword);
            }
            foreach (var innerKeyword in keyword.InnerKeywords.Values)
            {
                VisitKeywordChildren(innerKeyword, visitors);
            }
        }
    }

    #endregion /* Dynamic Keyword Reflection Type Loader */
}