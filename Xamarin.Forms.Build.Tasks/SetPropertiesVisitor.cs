using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

using Xamarin.Forms.Internals;
using Xamarin.Forms.Xaml;

using static Mono.Cecil.Cil.Instruction;
using static Mono.Cecil.Cil.OpCodes;

namespace Xamarin.Forms.Build.Tasks
{
	class SetPropertiesVisitor : IXamlNodeVisitor
	{
		static int dtcount;
		static int typedBindingCount;

		static readonly IList<XmlName> skips = new List<XmlName>
		{
			XmlName.xKey,
			XmlName.xTypeArguments,
			XmlName.xArguments,
			XmlName.xFactoryMethod,
			XmlName.xName,
			XmlName.xDataType
		};

		public SetPropertiesVisitor(ILContext context, bool stopOnResourceDictionary = false)
		{
			Context = context;
			Module = context.Body.Method.Module;
			StopOnResourceDictionary = stopOnResourceDictionary;
		}

		public ILContext Context { get; }
		public bool StopOnResourceDictionary { get; }
		public TreeVisitingMode VisitingMode => TreeVisitingMode.BottomUp;
		public bool StopOnDataTemplate => true;
		public bool VisitNodeOnDataTemplate => true;
		public bool SkipChildren(INode node, INode parentNode) => false;

		public bool IsResourceDictionary(ElementNode node)
		{
			var parentVar = Context.Variables[(IElementNode)node];
			return parentVar.VariableType.FullName == "Xamarin.Forms.ResourceDictionary"
				|| parentVar.VariableType.Resolve().BaseType?.FullName == "Xamarin.Forms.ResourceDictionary";
		}

		ModuleDefinition Module { get; }

		public void Visit(ValueNode node, INode parentNode)
		{
			//TODO support Label text as element
			XmlName propertyName;
			if (!TryGetPropertyName(node, parentNode, out propertyName))
			{
				if (!IsCollectionItem(node, parentNode))
					return;
				string contentProperty;
				if (!Context.Variables.ContainsKey((IElementNode)parentNode))
					return;
				var parentVar = Context.Variables[(IElementNode)parentNode];
				if ((contentProperty = GetContentProperty(parentVar.VariableType)) != null)
					propertyName = new XmlName(((IElementNode)parentNode).NamespaceURI, contentProperty);
				else
					return;
			}

			if (TrySetRuntimeName(propertyName, Context.Variables[(IElementNode)parentNode], node))
				return;
			if (skips.Contains(propertyName))
				return;
			if (parentNode is IElementNode && ((IElementNode)parentNode).SkipProperties.Contains (propertyName))
				return;
			if (propertyName.Equals(XamlParser.McUri, "Ignorable"))
				return;
			Context.IL.Append(SetPropertyValue(Context.Variables [(IElementNode)parentNode], propertyName, node, Context, node));
		}

		public void Visit(MarkupNode node, INode parentNode)
		{
		}

		public void Visit(ElementNode node, INode parentNode)
		{
			XmlName propertyName = XmlName.Empty;

			//Simplify ListNodes with single elements
			var pList = parentNode as ListNode;
			if (pList != null && pList.CollectionItems.Count == 1) {
				propertyName = pList.XmlName;
				parentNode = parentNode.Parent;
			}

			if ((propertyName != XmlName.Empty || TryGetPropertyName(node, parentNode, out propertyName)) && skips.Contains(propertyName))
				return;

			if (propertyName == XmlName._CreateContent) {
				SetDataTemplate((IElementNode)parentNode, node, Context, node);
				return;
			}

			//if this node is an IMarkupExtension, invoke ProvideValue() and replace the variable
			var vardef = Context.Variables[node];
			var vardefref = new VariableDefinitionReference(vardef);
			var localName = propertyName.LocalName;
			TypeReference declaringTypeReference = null;
			FieldReference bpRef = null;
			var _ = false;
			PropertyDefinition propertyRef = null;
			if (parentNode is IElementNode && propertyName != XmlName.Empty) {
				bpRef = GetBindablePropertyReference(Context.Variables [(IElementNode)parentNode], propertyName.NamespaceURI, ref localName, out _, Context, node);
				propertyRef = Context.Variables [(IElementNode)parentNode].VariableType.GetProperty(pd => pd.Name == localName, out declaringTypeReference);
			}
			Context.IL.Append(ProvideValue(vardefref, Context, Module, node, bpRef:bpRef, propertyRef:propertyRef, propertyDeclaringTypeRef: declaringTypeReference));
			if (vardef != vardefref.VariableDefinition)
			{
				vardef = vardefref.VariableDefinition;
				Context.Body.Variables.Add(vardef);
				Context.Variables[node] = vardef;
			}

			if (propertyName != XmlName.Empty) {
				if (skips.Contains(propertyName))
					return;
				if (parentNode is IElementNode && ((IElementNode)parentNode).SkipProperties.Contains (propertyName))
					return;
				
				Context.IL.Append(SetPropertyValue(Context.Variables[(IElementNode)parentNode], propertyName, node, Context, node));
			}
			else if (IsCollectionItem(node, parentNode) && parentNode is IElementNode) {
				var parentVar = Context.Variables[(IElementNode)parentNode];
				string contentProperty;

				if (CanAddToResourceDictionary(parentVar, parentVar.VariableType, node, node, Context)) {
					Context.IL.Append(parentVar.LoadAs(Module.GetTypeDefinition(("Xamarin.Forms.Core", "Xamarin.Forms", "ResourceDictionary")), Module));
					Context.IL.Append(AddToResourceDictionary(node, node, Context));
				}
				// Collection element, implicit content, or implicit collection element.
				else if (   parentVar.VariableType.ImplementsInterface(Module.ImportReference(("mscorlib", "System.Collections", "IEnumerable")))
						 && parentVar.VariableType.GetMethods(md => md.Name == "Add" && md.Parameters.Count == 1, Module).Any()) {
					var elementType = parentVar.VariableType;
					var adderTuple = elementType.GetMethods(md => md.Name == "Add" && md.Parameters.Count == 1, Module).First();
					var adderRef = Module.ImportReference(adderTuple.Item1);
					adderRef = Module.ImportReference(adderRef.ResolveGenericParameters(adderTuple.Item2, Module));

					Context.IL.Emit(Ldloc, parentVar);
					Context.IL.Append(vardef.LoadAs(adderRef.Parameters[0].ParameterType.ResolveGenericParameters(adderRef), Module));
					Context.IL.Emit(Callvirt, adderRef);
					if (adderRef.ReturnType.FullName != "System.Void")
						Context.IL.Emit(Pop);
				}
				else if ((contentProperty = GetContentProperty(parentVar.VariableType)) != null) {
					var name = new XmlName(node.NamespaceURI, contentProperty);
					if (skips.Contains(name))
						return;
					if (parentNode is IElementNode && ((IElementNode)parentNode).SkipProperties.Contains (propertyName))
						return;
					Context.IL.Append(SetPropertyValue(Context.Variables[(IElementNode)parentNode], name, node, Context, node));
				}
				else
					throw new XamlParseException($"Can not set the content of {((IElementNode)parentNode).XmlType.Name} as it doesn't have a ContentPropertyAttribute", node);
			}
			else if (IsCollectionItem(node, parentNode) && parentNode is ListNode)
			{
//				IL_000d:  ldloc.2 
//				IL_000e:  callvirt instance class [mscorlib]System.Collections.Generic.IList`1<!0> class [Xamarin.Forms.Core]Xamarin.Forms.Layout`1<class [Xamarin.Forms.Core]Xamarin.Forms.View>::get_Children()
//				IL_0013:  ldloc.0 
//				IL_0014:  callvirt instance void class [mscorlib]System.Collections.Generic.ICollection`1<class [Xamarin.Forms.Core]Xamarin.Forms.View>::Add(!0)

				var parentList = (ListNode)parentNode;
				var parent = Context.Variables[((IElementNode)parentNode.Parent)];

				if (skips.Contains(parentList.XmlName))
					return;
				if (parentNode is IElementNode && ((IElementNode)parentNode).SkipProperties.Contains (propertyName))
					return;
				var elementType = parent.VariableType;
				var localname = parentList.XmlName.LocalName;

				TypeReference propertyType;
				Context.IL.Append(GetPropertyValue(parent, parentList.XmlName, Context, node, out propertyType));

				if (CanAddToResourceDictionary(parent, propertyType, node, node, Context)) {
					Context.IL.Append(AddToResourceDictionary(node, node, Context));
					return;
				} 
				var adderTuple = propertyType.GetMethods(md => md.Name == "Add" && md.Parameters.Count == 1, Module).FirstOrDefault();
				if (adderTuple == null)
					throw new XamlParseException($"Can not Add() elements to {parent.VariableType}.{localname}", node);
				var adderRef = Module.ImportReference(adderTuple.Item1);
				adderRef = Module.ImportReference(adderRef.ResolveGenericParameters(adderTuple.Item2, Module));

				Context.IL.Append(vardef.LoadAs(adderRef.Parameters[0].ParameterType.ResolveGenericParameters(adderRef), Module));
				Context.IL.Emit(OpCodes.Callvirt, adderRef);
				if (adderRef.ReturnType.FullName != "System.Void")
						Context.IL.Emit(OpCodes.Pop);
			}
		}

		public void Visit(RootNode node, INode parentNode)
		{
		}

		public void Visit(ListNode node, INode parentNode)
		{
		}

		public static bool TryGetPropertyName(INode node, INode parentNode, out XmlName name)
		{
			name = default(XmlName);
			if (!(parentNode is IElementNode parentElement))
				return false;
			foreach (var kvp in parentElement.Properties)
			{
				if (kvp.Value != node)
					continue;
				name = kvp.Key;
				return true;
			}
			return false;
		}

		static bool IsCollectionItem(INode node, INode parentNode)
		{
			if (!(parentNode is IListNode parentList))
				return false;
			return parentList.CollectionItems.Contains(node);
		}

		internal static string GetContentProperty(TypeReference typeRef)
		{
			var typeDef = typeRef.ResolveCached();
			var attributes = typeDef.CustomAttributes;
			var attr =
				attributes.FirstOrDefault(cad => ContentPropertyAttribute.ContentPropertyTypes.Contains(cad.AttributeType.FullName));
			if (attr != null)
				return attr.ConstructorArguments[0].Value as string;
			if (typeDef.BaseType == null)
				return null;
			return GetContentProperty(typeDef.BaseType);
		}

		public static IEnumerable<Instruction> ProvideValue(VariableDefinitionReference vardefref, ILContext context,
		                                                    ModuleDefinition module, ElementNode node, FieldReference bpRef = null,
		                                                    PropertyReference propertyRef = null, TypeReference propertyDeclaringTypeRef = null)
		{
			GenericInstanceType markupExtension;
			IList<TypeReference> genericArguments;
			if (vardefref.VariableDefinition.VariableType.FullName == "Xamarin.Forms.Xaml.ArrayExtension" &&
			    vardefref.VariableDefinition.VariableType.ImplementsGenericInterface("Xamarin.Forms.Xaml.IMarkupExtension`1",
				    out markupExtension, out genericArguments))
			{
				var markExt = markupExtension.ResolveCached();
				var provideValueInfo = markExt.Methods.First(md => md.Name == "ProvideValue");
				var provideValue = module.ImportReference(provideValueInfo);
				provideValue =
					module.ImportReference(provideValue.ResolveGenericParameters(markupExtension, module));

				var typeNode = node.Properties[new XmlName("", "Type")];
				TypeReference arrayTypeRef;
				if (context.TypeExtensions.TryGetValue(typeNode, out arrayTypeRef))
					vardefref.VariableDefinition = new VariableDefinition(module.ImportReference(arrayTypeRef.MakeArrayType()));
				else
					vardefref.VariableDefinition = new VariableDefinition(module.ImportReference(genericArguments.First()));
				foreach (var instruction in context.Variables[node].LoadAs(markupExtension, module))
					yield return instruction;
				foreach (var instruction in node.PushServiceProvider(context, bpRef, propertyRef, propertyDeclaringTypeRef))
					yield return instruction;
				yield return Instruction.Create(OpCodes.Callvirt, provideValue);

				if (arrayTypeRef != null)
					yield return Instruction.Create(OpCodes.Castclass, module.ImportReference(arrayTypeRef.MakeArrayType()));
				yield return Instruction.Create(OpCodes.Stloc, vardefref.VariableDefinition);
			}
			else if (vardefref.VariableDefinition.VariableType.ImplementsGenericInterface("Xamarin.Forms.Xaml.IMarkupExtension`1",
				out markupExtension, out genericArguments))
			{
				var acceptEmptyServiceProvider = vardefref.VariableDefinition.VariableType.GetCustomAttribute(module, ("Xamarin.Forms.Core", "Xamarin.Forms.Xaml", "AcceptEmptyServiceProviderAttribute")) != null;
				if (   vardefref.VariableDefinition.VariableType.FullName == "Xamarin.Forms.Xaml.BindingExtension"
				    && (   node.Properties == null
				        || !node.Properties.ContainsKey(new XmlName("", "Source"))))
					foreach (var instruction in CompileBindingPath(node, context, vardefref.VariableDefinition))
						yield return instruction;

				var markExt = markupExtension.ResolveCached();
				var provideValueInfo = markExt.Methods.First(md => md.Name == "ProvideValue");
				var provideValue = module.ImportReference(provideValueInfo);
				provideValue =
					module.ImportReference(provideValue.ResolveGenericParameters(markupExtension, module));

				vardefref.VariableDefinition = new VariableDefinition(module.ImportReference(genericArguments.First()));
				foreach (var instruction in context.Variables[node].LoadAs(markupExtension, module))
					yield return instruction;
				if (acceptEmptyServiceProvider)
					yield return Instruction.Create(OpCodes.Ldnull);
				else
					foreach (var instruction in node.PushServiceProvider(context, bpRef, propertyRef, propertyDeclaringTypeRef))
						yield return instruction;
				yield return Instruction.Create(OpCodes.Callvirt, provideValue);
				yield return Instruction.Create(OpCodes.Stloc, vardefref.VariableDefinition);
			}
			else if (context.Variables[node].VariableType.ImplementsInterface(module.ImportReference(("Xamarin.Forms.Core", "Xamarin.Forms.Xaml", "IMarkupExtension"))))
			{
				var acceptEmptyServiceProvider = context.Variables[node].VariableType.GetCustomAttribute(module, ("Xamarin.Forms.Core", "Xamarin.Forms.Xaml", "AcceptEmptyServiceProviderAttribute")) != null;
				var markupExtensionType = ("Xamarin.Forms.Core", "Xamarin.Forms.Xaml", "IMarkupExtension");
				vardefref.VariableDefinition = new VariableDefinition(module.TypeSystem.Object);
				foreach (var instruction in context.Variables[node].LoadAs(module.GetTypeDefinition(markupExtensionType), module))
					yield return instruction;
				if (acceptEmptyServiceProvider)
					yield return Create(Ldnull);
				else
					foreach (var instruction in node.PushServiceProvider(context, bpRef, propertyRef, propertyDeclaringTypeRef))
						yield return instruction;
				yield return Create(Callvirt, module.ImportMethodReference(markupExtensionType,
																		   methodName: "ProvideValue",
																		   parameterTypes: new[] { ("System.ComponentModel", "System", "IServiceProvider") }));
				yield return Create(Stloc, vardefref.VariableDefinition);
			}
			else if (context.Variables[node].VariableType.ImplementsInterface(module.ImportReference(("Xamarin.Forms.Core", "Xamarin.Forms.Xaml", "IValueProvider"))))
			{
				var acceptEmptyServiceProvider = context.Variables[node].VariableType.GetCustomAttribute(module, ("Xamarin.Forms.Core", "Xamarin.Forms.Xaml", "AcceptEmptyServiceProviderAttribute")) != null;
				var valueProviderType = context.Variables[node].VariableType;
				//If the IValueProvider has a ProvideCompiledAttribute that can be resolved, shortcut this
				var compiledValueProviderName = valueProviderType?.GetCustomAttribute(module, ("Xamarin.Forms.Core", "Xamarin.Forms.Xaml", "ProvideCompiledAttribute"))?.ConstructorArguments?[0].Value as string;
				Type compiledValueProviderType;
				if (compiledValueProviderName != null && (compiledValueProviderType = Type.GetType(compiledValueProviderName)) != null) {
					var compiledValueProvider = Activator.CreateInstance(compiledValueProviderType);
					var cProvideValue = typeof(ICompiledValueProvider).GetMethods().FirstOrDefault(md => md.Name == "ProvideValue");
					var instructions = (IEnumerable<Instruction>)cProvideValue.Invoke(compiledValueProvider, new object[] {
						vardefref,
						context.Body.Method.Module,
						node as BaseNode,
						context});
					foreach (var i in instructions)
						yield return i;
					yield break;
				}

				var valueProviderInterface = ("Xamarin.Forms.Core", "Xamarin.Forms.Xaml", "IValueProvider");
				vardefref.VariableDefinition = new VariableDefinition(module.TypeSystem.Object);
				foreach (var instruction in context.Variables[node].LoadAs(module.GetTypeDefinition(valueProviderInterface), module))
					yield return instruction;
				if (acceptEmptyServiceProvider)
					yield return Create(Ldnull);
				else
					foreach (var instruction in node.PushServiceProvider(context, bpRef, propertyRef, propertyDeclaringTypeRef))
						yield return instruction;
				yield return Create(Callvirt, module.ImportMethodReference(valueProviderInterface,
																		   methodName: "ProvideValue",
																		   parameterTypes: new[] { ("System.ComponentModel", "System", "IServiceProvider") }));
				yield return Create(Stloc, vardefref.VariableDefinition);
			}
		}

		//Once we get compiled IValueProvider, this will move to the BindingExpression
		static IEnumerable<Instruction> CompileBindingPath(ElementNode node, ILContext context, VariableDefinition bindingExt)
		{
			//TODO support casting operators
			var module = context.Module;

			if (!node.Properties.TryGetValue(new XmlName("", "Path"), out INode pathNode) && node.CollectionItems.Any())
				pathNode = node.CollectionItems[0];
			var path = (pathNode as ValueNode)?.Value as string;
			if (   !node.Properties.TryGetValue(new XmlName("", "Mode"), out INode modeNode)
				|| !Enum.TryParse((modeNode as ValueNode)?.Value as string, true, out BindingMode declaredmode))
				declaredmode = BindingMode.TwoWay;  //meaning the mode isn't specified in the Binding extension. generate getters, setters, handlers

			INode dataTypeNode = null;
			IElementNode n = node;
			while (n != null) {
				if (n.Properties.TryGetValue(XmlName.xDataType, out dataTypeNode))
					break;
				n = n.Parent as IElementNode;
			}

			if (dataTypeNode is null)
				yield break;

			if (   dataTypeNode is ElementNode enode
				&& enode.XmlType.NamespaceUri == XamlParser.X2009Uri
				&& enode.XmlType.Name == "NullExtension")
				yield break;

			if (!((dataTypeNode as ValueNode)?.Value is string dataType))
				throw new XamlParseException("x:DataType expects a string literal", dataTypeNode as IXmlLineInfo);

			var prefix = dataType.Contains(":") ? dataType.Substring(0, dataType.IndexOf(":", StringComparison.Ordinal)) : "";
			var namespaceuri = node.NamespaceResolver.LookupNamespace(prefix) ?? "";
			if (!string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(namespaceuri))
				throw new XamlParseException($"Undeclared xmlns prefix '{prefix}'", dataTypeNode as IXmlLineInfo);

			var dtXType = new XmlType(namespaceuri, dataType, null);

			var tSourceRef = dtXType.GetTypeReference(module, (IXmlLineInfo)node);
			if (tSourceRef == null)
				yield break; //throw

			var properties = ParsePath(path, tSourceRef, node as IXmlLineInfo, module);
			TypeReference tPropertyRef = tSourceRef;
			if (properties != null && properties.Count > 0) {
				var lastProp = properties[properties.Count - 1];
				tPropertyRef = lastProp.property.PropertyType.ResolveGenericParameters(lastProp.propDeclTypeRef);
			}
			tPropertyRef = module.ImportReference(tPropertyRef);
			var valuetupleRef = context.Module.ImportReference(module.ImportReference(("mscorlib", "System", "ValueTuple`2")).MakeGenericInstanceType(new[] { tPropertyRef, module.TypeSystem.Boolean }));
			var funcRef = module.ImportReference(module.ImportReference(("mscorlib", "System", "Func`2")).MakeGenericInstanceType(new [] { tSourceRef, valuetupleRef }));
			var actionRef = module.ImportReference(module.ImportReference(("mscorlib", "System", "Action`2")).MakeGenericInstanceType(new [] { tSourceRef, tPropertyRef }));
			var funcObjRef = module.ImportReference(module.ImportReference(("mscorlib", "System", "Func`2")).MakeGenericInstanceType(new [] { tSourceRef, module.TypeSystem.Object }));
			var tupleRef = module.ImportReference(module.ImportReference(("mscorlib", "System", "Tuple`2")).MakeGenericInstanceType(new [] { funcObjRef, module.TypeSystem.String}));
			var typedBindingRef = module.ImportReference(module.ImportReference(("Xamarin.Forms.Core", "Xamarin.Forms.Internals", "TypedBinding`2")).MakeGenericInstanceType(new [] { tSourceRef, tPropertyRef}));

			//FIXME: make sure the non-deprecated one is used
			var ctorInfo =  module.ImportReference(typedBindingRef.ResolveCached().Methods.FirstOrDefault(md => 
					   md.IsConstructor
					&& !md.IsStatic
					&& md.Parameters.Count == 3
					&& !md.HasCustomAttributes (module.ImportReference(("mscorlib", "System", "ObsoleteAttribute")))));
			var ctorinforef = ctorInfo.MakeGeneric(typedBindingRef, funcRef, actionRef, tupleRef);

			var bindingExtensionType = ("Xamarin.Forms.Xaml", "Xamarin.Forms.Xaml", "BindingExtension");

			foreach (var instruction in bindingExt.LoadAs(module.GetTypeDefinition(bindingExtensionType), module))
				yield return instruction;
			foreach (var instruction in CompiledBindingGetGetter(tSourceRef, tPropertyRef, properties, node, context))
				yield return instruction;
			if (declaredmode != BindingMode.OneTime && declaredmode != BindingMode.OneWay) { //if the mode is explicitly 1w, or 1t, no need for setters
				foreach (var instruction in CompiledBindingGetSetter(tSourceRef, tPropertyRef, properties, node, context))
					yield return instruction;
			} else
				yield return Create(Ldnull);
			if (declaredmode != BindingMode.OneTime) { //if the mode is explicitly 1t, no need for handlers
				foreach (var instruction in CompiledBindingGetHandlers(tSourceRef, tPropertyRef, properties, node, context))
					yield return instruction;
			} else
				yield return Create(Ldnull);
			yield return Create(Newobj, module.ImportReference(ctorinforef));
			yield return Create(Callvirt, module.ImportPropertySetterReference(bindingExtensionType, propertyName: "TypedBinding"));
		}

		static IList<(PropertyDefinition property, TypeReference propDeclTypeRef, string indexArg)> ParsePath(string path, TypeReference tSourceRef, IXmlLineInfo lineInfo, ModuleDefinition module)
		{
			if (string.IsNullOrWhiteSpace(path))
				return null;
			path = path.Trim(' ', '.'); //trim leading or trailing dots
			var parts = path.Split(new [] { '.' }, StringSplitOptions.RemoveEmptyEntries);
			var properties = new List<(PropertyDefinition property, TypeReference propDeclTypeRef, string indexArg)>();

			var previousPartTypeRef = tSourceRef;
			foreach (var part in parts) {
				var p = part;
				string indexArg = null;
				var lbIndex = p.IndexOf('[');
				if (lbIndex != -1) {
					var rbIndex = p.LastIndexOf(']');
					if (rbIndex == -1)
						throw new XamlParseException("Binding: Indexer did not contain closing bracket", lineInfo);
					
					var argLength = rbIndex - lbIndex - 1;
					if (argLength == 0)
						throw new XamlParseException("Binding: Indexer did not contain arguments", lineInfo);

					indexArg = p.Substring(lbIndex + 1, argLength).Trim();
					if (indexArg.Length == 0)
						throw new XamlParseException("Binding: Indexer did not contain arguments", lineInfo);
					
					p = p.Substring(0, lbIndex);
					p = p.Trim();
				}

				if (p.Length > 0) {
					var property = previousPartTypeRef.GetProperty(pd => pd.Name == p && pd.GetMethod != null && pd.GetMethod.IsPublic, out var propDeclTypeRef)
					                                  ?? throw new XamlParseException($"Binding: Property '{p}' not found on '{previousPartTypeRef}'", lineInfo);
					properties.Add((property, propDeclTypeRef, null));
					previousPartTypeRef = property.PropertyType.ResolveGenericParameters(propDeclTypeRef);
				}
				if (indexArg != null) {
					var defaultMemberAttribute = previousPartTypeRef.GetCustomAttribute(module, ("mscorlib", "System.Reflection", "DefaultMemberAttribute"));
					var indexerName = defaultMemberAttribute?.ConstructorArguments?.FirstOrDefault().Value as string ?? "Item";
					var indexer = previousPartTypeRef.GetProperty(pd => pd.Name == indexerName && pd.GetMethod != null && pd.GetMethod.IsPublic, out var indexerDeclTypeRef);
					properties.Add((indexer, indexerDeclTypeRef, indexArg));
					var indexType = indexer.GetMethod.Parameters[0].ParameterType.ResolveGenericParameters(indexerDeclTypeRef);
					if (!TypeRefComparer.Default.Equals(indexType, module.TypeSystem.String) && !TypeRefComparer.Default.Equals(indexType, module.TypeSystem.Int32))
						throw new XamlParseException($"Binding: Unsupported indexer index type: {indexType.FullName}", lineInfo);
					previousPartTypeRef = indexer.PropertyType.ResolveGenericParameters(indexerDeclTypeRef);
				}
			}
			return properties;
		}

		static IEnumerable<Instruction> DigProperties(IEnumerable<(PropertyDefinition property, TypeReference propDeclTypeRef, string indexArg)> properties, Dictionary<TypeReference, VariableDefinition> locs, Func<Instruction> fallback, IXmlLineInfo lineInfo, ModuleDefinition module)
		{
			var first = true;

			foreach (var (property, propDeclTypeRef, indexArg) in properties) {
				if (!first && propDeclTypeRef.IsValueType) {
					var importedPropDeclTypeRef = module.ImportReference(propDeclTypeRef);

					if (!locs.TryGetValue(importedPropDeclTypeRef, out var loc)) {
						loc = new VariableDefinition(importedPropDeclTypeRef);
						locs[importedPropDeclTypeRef] = loc;
					}

					yield return Create(Stloc, loc);
					yield return Create(Ldloca, loc);
				}

				if (fallback != null && !propDeclTypeRef.IsValueType) {
					yield return Create(Dup);
					yield return Create(Brfalse, fallback());
				}

				if (indexArg != null) {
					var indexType = property.GetMethod.Parameters[0].ParameterType.ResolveGenericParameters(propDeclTypeRef);
					if (TypeRefComparer.Default.Equals(indexType, module.TypeSystem.String))
						yield return Create(Ldstr, indexArg);
					else if (TypeRefComparer.Default.Equals(indexType, module.TypeSystem.Int32) && int.TryParse(indexArg, out int index))
						yield return Create(Ldc_I4, index);
					else
						throw new XamlParseException($"Binding: {indexArg} could not be parsed as an index for a {property.Name}", lineInfo);
				}

				var getMethod = module.ImportReference((module.ImportReference(property.GetMethod)).ResolveGenericParameters(propDeclTypeRef, module));

				if (property.GetMethod.IsVirtual)
					yield return Create(Callvirt, getMethod);
				else
					yield return Create(Call, getMethod);

				first = false;
			}
		}

		static IEnumerable<Instruction> CompiledBindingGetGetter(TypeReference tSourceRef, TypeReference tPropertyRef, IList<(PropertyDefinition property, TypeReference propDeclTypeRef, string indexArg)> properties, ElementNode node, ILContext context)
		{
//				.method private static hidebysig default valuetype[mscorlib] System.ValueTuple`2<string, bool> '<Main>m__0' (class ViewModel A_0)  cil managed
//				{
//					.custom instance void class [mscorlib] System.Runtime.CompilerServices.CompilerGeneratedAttribute::'.ctor'() =  (01 00 00 00 ) // ....
//					IL_0000:  ldarg.0 
//					IL_0001:  dup
//					IL_0002:  ldnull
//					IL_0003:  ceq
//					IL_0005:  brfalse IL_0013
//					IL_000a:  pop
//					IL_000b:  ldnull
//					IL_000c:  ldc.i4.0 
//					IL_000d:  newobj instance void valuetype[mscorlib]System.ValueTuple`2<string, bool>::'.ctor'(!0, !1)
//					IL_0012:  ret
//					IL_0013:  nop
//					IL_0014:  call instance string class ViewModel::get_Text()
//					IL_0019:  ldc.i4.1 
//					IL_001a:  newobj instance void valuetype[mscorlib]System.ValueTuple`2<string, bool>::'.ctor'(!0, !1)
//					IL_001f:  ret
//				}

			var module = context.Module;
			var tupleRef = module.ImportReference(module.ImportReference(("mscorlib", "System", "ValueTuple`2")).MakeGenericInstanceType(new[] { tPropertyRef, module.TypeSystem.Boolean }));
			var tupleCtorRef = module.ImportCtorReference(tupleRef, 2);
			tupleCtorRef = module.ImportReference(tupleCtorRef.ResolveGenericParameters(tupleRef, module));
			var getter = new MethodDefinition($"<{context.Body.Method.Name}>typedBindingsM__{typedBindingCount++}",
											  MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Static,
											  tupleRef) {
				Parameters = { new ParameterDefinition(tSourceRef) },
				CustomAttributes = { new CustomAttribute (module.ImportCtorReference(("mscorlib", "System.Runtime.CompilerServices", "CompilerGeneratedAttribute"), parameterTypes: null)) }
			};

			getter.Body.InitLocals = true;
			var il = getter.Body.GetILProcessor();

			if (properties == null || properties.Count == 0) { //return self
				il.Emit(Ldarg_0);
				il.Emit(Ldc_I4_1); //true
				il.Emit(Newobj, tupleCtorRef);
				il.Emit(Ret);
			}
			else {
				var locs = new Dictionary<TypeReference, VariableDefinition>();

				if (tSourceRef.IsValueType)
					il.Emit(Ldarga_S, (byte)0);
				else
					il.Emit(Ldarg_0);

				Instruction pop = null;
				il.Append(DigProperties(properties, locs, () => {
					if (pop == null)
						pop = Create(Pop);

					return pop;
				}, node as IXmlLineInfo, module));

				foreach (var loc in locs.Values)
					getter.Body.Variables.Add(loc);

				il.Emit(Ldc_I4_1); //true
				il.Emit(Newobj, tupleCtorRef);
				il.Emit(Ret);

				if (pop != null) {
					if (!locs.TryGetValue(tupleRef, out var defaultValueVarDef)) {
						defaultValueVarDef = new VariableDefinition(tupleRef);
						getter.Body.Variables.Add(defaultValueVarDef);
					}

					il.Append(pop);
					il.Emit(Ldloca_S, defaultValueVarDef);
					il.Emit(Initobj, tupleRef);
					il.Emit(Ldloc, defaultValueVarDef);
					il.Emit(Ret);
				}
			}
			context.Body.Method.DeclaringType.Methods.Add(getter);

//			IL_02fa:  ldnull
//			IL_02fb:  ldftn valuetype[mscorlib]System.ValueTuple`2 <string,bool> class Test::'<Main>m__0'(class ViewModel)
//			IL_0301:  newobj instance void class [mscorlib] System.Func`2<class ViewModel, valuetype[mscorlib] System.ValueTuple`2<string, bool>>::'.ctor'(object, native int)
			yield return Create(Ldnull);
			yield return Create(Ldftn, getter);
			yield return Create(Newobj, module.ImportCtorReference(("mscorlib", "System", "Func`2"), paramCount: 2, classArguments: new[] { tSourceRef, tupleRef }));
		}

		static IEnumerable<Instruction> CompiledBindingGetSetter(TypeReference tSourceRef, TypeReference tPropertyRef, IList<(PropertyDefinition property, TypeReference propDeclTypeRef, string indexArg)> properties, ElementNode node, ILContext context)
		{
			if (properties == null || properties.Count == 0) {
				yield return Create(Ldnull);
				yield break;
			}

//			.method private static hidebysig default void '<Main>m__1' (class ViewModel vm, string s)  cil managed
//			{
//				.custom instance void class [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::'.ctor'() =  (01 00 00 00 ) // ....
//
//				IL_0000:  ldarg.0 
//				IL_0001:  callvirt instance class ViewModel class ViewModel::get_Model()
//				IL_0006:  ldarg.1 
//				IL_0007:  callvirt instance void class ViewModel::set_Text(string)
//				IL_000c:  ret
//			}

			var module = context.Module;
			var setter = new MethodDefinition($"<{context.Body.Method.Name}>typedBindingsM__{typedBindingCount++}",
											  MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Static,
			                                  module.TypeSystem.Void) {
				Parameters = {
					new ParameterDefinition(tSourceRef),
					new ParameterDefinition(tPropertyRef)
				},
				CustomAttributes = {
					new CustomAttribute (module.ImportCtorReference(("mscorlib", "System.Runtime.CompilerServices", "CompilerGeneratedAttribute"), parameterTypes: null))
				}
			};
			setter.Body.InitLocals = true;

			var il = setter.Body.GetILProcessor();
			if (!properties.Any() || properties.Last().property.SetMethod == null) {
				yield return Create(Ldnull); //throw or not ?
				yield break;
			}

			var setterRef = module.ImportReference(properties.Last().property.SetMethod);
			setterRef = module.ImportReference(setterRef.ResolveGenericParameters(properties.Last().propDeclTypeRef, module));

			if (tSourceRef.IsValueType)
				il.Emit(Ldarga_S, (byte)0);
			else
				il.Emit(Ldarg_0);
			var locs = new Dictionary<TypeReference, VariableDefinition>();
			Instruction pop = null;
			il.Append(DigProperties(properties.Take(properties.Count - 1), locs, () => {
				if (pop == null)
					pop = Instruction.Create(Pop);

				return pop;
			}, node as IXmlLineInfo, module));

			foreach (var loc in locs.Values)
				setter.Body.Variables.Add(loc);

			(PropertyDefinition lastProperty, TypeReference lastPropDeclTypeRef, string lastIndexArg) = properties.Last();
			if (lastPropDeclTypeRef.IsValueType) {
				var importedPropDeclTypeRef = module.ImportReference(lastPropDeclTypeRef);

				if (!locs.TryGetValue(importedPropDeclTypeRef, out var loc)) {
					loc = new VariableDefinition(importedPropDeclTypeRef);
					setter.Body.Variables.Add(loc);
				}

				il.Emit(Stloc, loc);
				il.Emit(Ldloca, loc);
			} else {
				if (pop == null)
					pop = Instruction.Create(Pop);

				il.Emit(Dup);
				il.Emit(Brfalse, pop);
			}

			if (lastIndexArg != null) {
				var indexType = lastProperty.GetMethod.Parameters[0].ParameterType.ResolveGenericParameters(lastPropDeclTypeRef);
				if (TypeRefComparer.Default.Equals(indexType, module.TypeSystem.String))
					il.Emit(Ldstr, lastIndexArg);
				else if (TypeRefComparer.Default.Equals(indexType, module.TypeSystem.Int32)) {
					if (!int.TryParse(lastIndexArg, out int index))
						throw new XamlParseException($"Binding: {lastIndexArg} could not be parsed as an index for a {lastProperty.Name}", node as IXmlLineInfo);
					il.Emit(Ldc_I4, index);
				}
			}

			il.Emit(Ldarg_1);

			if (properties.Last().property.SetMethod.IsVirtual)
				il.Emit(Callvirt, setterRef);
			else
				il.Emit(Call, setterRef);

			il.Emit(Ret);

			if (pop != null) {
				il.Append(pop);
				il.Emit(Ret);
			}

			context.Body.Method.DeclaringType.Methods.Add(setter);

//			IL_0024: ldnull
//			IL_0025: ldftn void class Test::'<Main>m__1'(class ViewModel, string)
//			IL_002b: newobj instance void class [mscorlib]System.Action`2<class ViewModel, string>::'.ctor'(object, native int)
			yield return Create(Ldnull);
			yield return Create(Ldftn, setter);
			yield return Create(Newobj, module.ImportCtorReference(("mscorlib", "System", "Action`2"),
																   paramCount: 2,
																   classArguments:
																   new[] { tSourceRef, tPropertyRef }));
		}

		static IEnumerable<Instruction> CompiledBindingGetHandlers(TypeReference tSourceRef, TypeReference tPropertyRef, IList<(PropertyDefinition property, TypeReference propDeclTypeRef, string indexArg)> properties, ElementNode node, ILContext context)
		{
//			.method private static hidebysig default object '<Main>m__2'(class ViewModel vm)  cil managed {
//				.custom instance void class [mscorlib] System.Runtime.CompilerServices.CompilerGeneratedAttribute::'.ctor'() =  (01 00 00 00 ) // ....
//				IL_0000:  ldarg.0 
//				IL_0001:  ret
//			} // end of method Test::<Main>m__2

//			.method private static hidebysig default object '<Main>m__3' (class ViewModel vm)  cil managed {
//				.custom instance void class [mscorlib] System.Runtime.CompilerServices.CompilerGeneratedAttribute::'.ctor'() =  (01 00 00 00 ) // ....
//				IL_0000:  ldarg.0 
//				IL_0001:  callvirt instance class ViewModel class ViewModel::get_Model()
//				IL_0006:  ret
//			}

			var module = context.Module;

			var partGetters = new List<MethodDefinition>();
			if (properties == null || properties.Count == 0) {
				yield return Create(Ldnull);
				yield break;
			}
				
			for (int i = 0; i < properties.Count; i++) {
				var tuple = properties [i];
				var partGetter = new MethodDefinition($"<{context.Body.Method.Name}>typedBindingsM__{typedBindingCount++}", MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Static, module.TypeSystem.Object) {
					Parameters = {
						new ParameterDefinition(tSourceRef)
					},
					CustomAttributes = {
						new CustomAttribute (module.ImportCtorReference(("mscorlib", "System.Runtime.CompilerServices", "CompilerGeneratedAttribute"), parameterTypes: null))
					}
				};
				partGetter.Body.InitLocals = true;
				var il = partGetter.Body.GetILProcessor();

				if (i == 0) { //return self
					il.Emit(Ldarg_0);
					if (tSourceRef.IsValueType)
						il.Emit(Box, module.ImportReference(tSourceRef));

					il.Emit(Ret);
					context.Body.Method.DeclaringType.Methods.Add(partGetter);
					partGetters.Add(partGetter);
					continue;
				}

				if (tSourceRef.IsValueType)
					il.Emit(Ldarga_S, (byte)0);
				else
					il.Emit(Ldarg_0);
				var lastGetterTypeRef = properties[i - 1].property.PropertyType;
				var locs = new Dictionary<TypeReference, VariableDefinition>();
				il.Append(DigProperties(properties.Take(i), locs, null, node as IXmlLineInfo, module));
				foreach (var loc in locs.Values)
					partGetter.Body.Variables.Add(loc);
				if (lastGetterTypeRef.IsValueType)
					il.Emit(Box, module.ImportReference(lastGetterTypeRef));

				il.Emit(Ret);
				context.Body.Method.DeclaringType.Methods.Add(partGetter);
				partGetters.Add(partGetter);
			}

			var funcObjRef = context.Module.ImportReference(module.ImportReference(("mscorlib", "System", "Func`2")).MakeGenericInstanceType(new [] { tSourceRef, module.TypeSystem.Object }));
			var tupleRef = context.Module.ImportReference(module.ImportReference(("mscorlib", "System", "Tuple`2")).MakeGenericInstanceType(new [] { funcObjRef, module.TypeSystem.String }));
			var funcCtor = module.ImportReference(funcObjRef.ResolveCached().GetConstructors().First());
			funcCtor = funcCtor.MakeGeneric(funcObjRef, new [] { tSourceRef, module.TypeSystem.Object });
			var tupleCtor = module.ImportReference(tupleRef.ResolveCached().GetConstructors().First());
			tupleCtor = tupleCtor.MakeGeneric(tupleRef, new [] { funcObjRef, module.TypeSystem.String});

//			IL_003a:  ldc.i4.2 
//			IL_003b:  newarr class [mscorlib] System.Tuple`2<class [mscorlib]System.Func`2<class ViewModel,object>,string>

//			IL_0040:  dup
//			IL_0041:  ldc.i4.0 
//			IL_0049:  ldnull
//			IL_004a:  ldftn object class Test::'<Main>m__2'(class ViewModel)
//			IL_0050:  newobj instance void class [mscorlib]System.Func`2<class ViewModel, object>::'.ctor'(object, native int)
//			IL_005f:  ldstr "Model"
//			IL_0064:  newobj instance void class [mscorlib]System.Tuple`2<class [mscorlib]System.Func`2<class ViewModel, object>, string>::'.ctor'(!0, !1)
//			IL_0069:  stelem.ref 

//			IL_006a:  dup
//			IL_006b:  ldc.i4.1 
//			IL_0073:  ldnull
//			IL_0074:  ldftn object class Test::'<Main>m__3'(class ViewModel)
//			IL_007a:  newobj instance void class [mscorlib]System.Func`2<class ViewModel, object>::'.ctor'(object, native int)
//			IL_0089:  ldstr "Text"
//			IL_008e:  newobj instance void class [mscorlib]System.Tuple`2<class [mscorlib]System.Func`2<class ViewModel, object>, string>::'.ctor'(!0, !1)
//			IL_0093:  stelem.ref 

			yield return Instruction.Create(OpCodes.Ldc_I4, properties.Count);
			yield return Instruction.Create(OpCodes.Newarr, tupleRef);

			for (var i = 0; i < properties.Count; i++) {
				yield return Instruction.Create(OpCodes.Dup);
				yield return Instruction.Create(OpCodes.Ldc_I4, i);
				yield return Instruction.Create(OpCodes.Ldnull);
				yield return Instruction.Create(OpCodes.Ldftn, partGetters [i]);
				yield return Instruction.Create(OpCodes.Newobj, module.ImportReference(funcCtor));
				yield return Instruction.Create(OpCodes.Ldstr, properties [i].Item1.Name);
				yield return Instruction.Create(OpCodes.Newobj, module.ImportReference(tupleCtor));
				yield return Instruction.Create(OpCodes.Stelem_Ref);
			}
		}

		public static IEnumerable<Instruction> SetPropertyValue(VariableDefinition parent, XmlName propertyName, INode valueNode, ILContext context, IXmlLineInfo iXmlLineInfo)
		{
			var localName = propertyName.LocalName;
			var bpRef = GetBindablePropertyReference(parent, propertyName.NamespaceURI, ref localName, out System.Boolean attached, context, iXmlLineInfo);

			//If the target is an event, connect
			if (CanConnectEvent(parent, localName, valueNode, attached))
				return ConnectEvent(parent, localName, valueNode, iXmlLineInfo, context);

			//If Value is DynamicResource, SetDynamicResource
			if (CanSetDynamicResource(bpRef, valueNode, context))
				return SetDynamicResource(parent, bpRef, valueNode as IElementNode, iXmlLineInfo, context);

			//If Value is a BindingBase and target is a BP, SetBinding
			if (CanSetBinding(bpRef, valueNode, context))
				return SetBinding(parent, bpRef, valueNode as IElementNode, iXmlLineInfo, context);

			//If it's a BP, SetValue ()
			if (CanSetValue(bpRef, attached, valueNode, iXmlLineInfo, context))
				return SetValue(parent, bpRef, valueNode, iXmlLineInfo, context);

			//If it's a property, set it
			if (CanSet(parent, localName, valueNode, context))
				return Set(parent, localName, valueNode, iXmlLineInfo, context);

			//If it's an already initialized property, add to it
			if (CanAdd(parent, propertyName, valueNode, iXmlLineInfo, context))
				return Add(parent, propertyName, valueNode, iXmlLineInfo, context);

			throw new XamlParseException($"No property, bindable property, or event found for '{localName}', or mismatching type between value and property.", iXmlLineInfo);
		}

		public static IEnumerable<Instruction> GetPropertyValue(VariableDefinition parent, XmlName propertyName, ILContext context, IXmlLineInfo lineInfo, out TypeReference propertyType)
		{
			var module = context.Body.Method.Module;
			var localName = propertyName.LocalName;
			bool attached;
			var bpRef = GetBindablePropertyReference(parent, propertyName.NamespaceURI, ref localName, out attached, context, lineInfo);

			//If it's a BP, GetValue ()
			if (CanGetValue(parent, bpRef, attached, lineInfo, context, out _))
				return GetValue(parent, bpRef, lineInfo, context, out propertyType);

			//If it's a property, set it
			if (CanGet(parent, localName, context, out _))
				return Get(parent, localName, lineInfo, context, out propertyType);

			throw new XamlParseException($"Property {localName} is not found or does not have an accessible getter", lineInfo);
		}

		static FieldReference GetBindablePropertyReference(VariableDefinition parent, string namespaceURI, ref string localName, out bool attached, ILContext context, IXmlLineInfo iXmlLineInfo)
		{
			var module = context.Body.Method.Module;
			TypeReference declaringTypeReference;

			//If it's an attached BP, update elementType and propertyName
			var bpOwnerType = parent.VariableType;
			attached = GetNameAndTypeRef(ref bpOwnerType, namespaceURI, ref localName, context, iXmlLineInfo);
			var name = $"{localName}Property";
			FieldReference bpRef = bpOwnerType.GetField(fd => fd.Name == name &&
														fd.IsStatic &&
														(fd.IsPublic || fd.IsAssembly), out declaringTypeReference);
			if (bpRef != null) {
				bpRef = module.ImportReference(bpRef.ResolveGenericParameters(declaringTypeReference));
				bpRef.FieldType = module.ImportReference(bpRef.FieldType);
			}
			return bpRef;
		}

		static bool CanConnectEvent(VariableDefinition parent, string localName, INode valueNode, bool attached)
		{
			return !attached && valueNode is ValueNode && parent.VariableType.GetEvent(ed => ed.Name == localName, out _) != null;
		}

		static IEnumerable<Instruction> ConnectEvent(VariableDefinition parent, string localName, INode valueNode, IXmlLineInfo iXmlLineInfo, ILContext context)
		{
			var elementType = parent.VariableType;
			var module = context.Body.Method.Module;
			TypeReference eventDeclaringTypeRef;
			var eventinfo = elementType.GetEvent(ed => ed.Name == localName, out eventDeclaringTypeRef);
			var adder = module.ImportReference(eventinfo.AddMethod);
			adder = adder.ResolveGenericParameters(eventDeclaringTypeRef, module);

//			IL_0007:  ldloc.0 
//			IL_0008:  ldarg.0 
//
//			IL_0009:  ldftn instance void class Xamarin.Forms.Xaml.XamlcTests.MyPage::OnButtonClicked(object, class [mscorlib]System.EventArgs)
//OR, if the handler is virtual
//			IL_000x:  ldarg.0 
//			IL_0009:  ldvirtftn instance void class Xamarin.Forms.Xaml.XamlcTests.MyPage::OnButtonClicked(object, class [mscorlib]System.EventArgs)
//
//			IL_000f:  newobj instance void class [mscorlib]System.EventHandler::'.ctor'(object, native int)
//			IL_0014:  callvirt instance void class [Xamarin.Forms.Core]Xamarin.Forms.Button::add_Clicked(class [mscorlib]System.EventHandler)

			var value = ((ValueNode)valueNode).Value;

			yield return Create(Ldloc, parent);
			var declaringType = context.Body.Method.DeclaringType;
			while (declaringType.IsNested)
				declaringType = declaringType.DeclaringType;
			var handler = declaringType.AllMethods().FirstOrDefault(md => {
				if (md.methodDef.Name != value as string)
					return false;

				//check if the handler signature matches the Invoke signature;
				var invoke = module.ImportReference(eventinfo.EventType.ResolveCached().GetMethods().First(eventmd => eventmd.Name == "Invoke"));
				invoke = invoke.ResolveGenericParameters(eventinfo.EventType, module);
				if (!md.methodDef.ReturnType.InheritsFromOrImplements(invoke.ReturnType) || invoke.Parameters.Count != md.methodDef.Parameters.Count)
					return false;

				if (!invoke.ContainsGenericParameter)
					for (var i = 0; i < invoke.Parameters.Count;i++)
						if (!invoke.Parameters[i].ParameterType.InheritsFromOrImplements(md.methodDef.Parameters[i].ParameterType))
							return false;
				//TODO check generic parameters if any

				return true;
			});
			MethodReference handlerRef = null;
			if (handler.methodDef != null)
				handlerRef = handler.methodDef.ResolveGenericParameters(handler.declTypeRef, module);
			if (handler.methodDef == null) 
				throw new XamlParseException($"EventHandler \"{value}\" with correct signature not found in type \"{declaringType}\"", iXmlLineInfo);

			//FIXME: eventually get the right ctor instead fo the First() one, just in case another one could exists (not even sure it's possible).
			var ctor = module.ImportReference(eventinfo.EventType.ResolveCached().GetConstructors().First());
			ctor = ctor.ResolveGenericParameters(eventinfo.EventType, module);

			if (handler.methodDef.IsStatic) {
				yield return Create(Ldnull);
			} else {
				if (context.Root is VariableDefinition)
					foreach (var instruction in (context.Root as VariableDefinition).LoadAs(ctor.Parameters[0].ParameterType.ResolveGenericParameters(ctor), module))
						yield return instruction;
				else if (context.Root is FieldDefinition) {
					yield return Create(Ldarg_0);
					yield return Create(Ldfld, context.Root as FieldDefinition);
				} else 
					throw new InvalidProgramException();
			}

			if (handler.methodDef.IsVirtual) {
				yield return Create(Ldarg_0);
				yield return Create(Ldvirtftn, handlerRef);
			} else
				yield return Create(Ldftn, handlerRef);

			yield return Create(Newobj, module.ImportReference(ctor));
			//Check if the handler has the same signature as the ctor (it should)
			yield return Create(Callvirt, module.ImportReference(adder));
		}

		static bool CanSetDynamicResource(FieldReference bpRef, INode valueNode, ILContext context)
		{
			if (bpRef == null)
				return false;
			var elementNode = valueNode as IElementNode;
			if (elementNode == null)
				return false;
			
			VariableDefinition varValue;
			if (!context.Variables.TryGetValue(valueNode as IElementNode, out varValue))
				return false;
			return varValue.VariableType.FullName == typeof(DynamicResource).FullName;
		}

		static IEnumerable<Instruction> SetDynamicResource(VariableDefinition parent, FieldReference bpRef, IElementNode elementNode, IXmlLineInfo iXmlLineInfo, ILContext context)
		{
			var module = context.Body.Method.Module;
			var dynamicResourceType = ("Xamarin.Forms.Core", "Xamarin.Forms.Internals", "DynamicResource");
			var dynamicResourceHandlerType = ("Xamarin.Forms.Core", "Xamarin.Forms.Internals", "IDynamicResourceHandler");

			foreach (var instruction in parent.LoadAs(module.GetTypeDefinition(dynamicResourceHandlerType), module))
				yield return instruction;
			yield return Create(Ldsfld, bpRef);
			foreach (var instruction in context.Variables[elementNode].LoadAs(module.GetTypeDefinition(dynamicResourceType), module))
				yield return instruction;
			yield return Create(Callvirt, module.ImportPropertyGetterReference(dynamicResourceType, propertyName: "Key"));
			yield return Create(Callvirt, module.ImportMethodReference(dynamicResourceHandlerType,
																	   methodName: "SetDynamicResource",
																	   parameterTypes: new[] {
																		   ("Xamarin.Forms.Core", "Xamarin.Forms", "BindableProperty"),
																		   ("mscorlib", "System", "String"),
																	   }));
		}

		static bool CanSetBinding(FieldReference bpRef, INode valueNode, ILContext context)
		{
			var module = context.Body.Method.Module;

			if (bpRef == null)
				return false;
			if (!(valueNode is IElementNode elementNode))
				return false;

			if (!context.Variables.TryGetValue(valueNode as IElementNode, out VariableDefinition varValue))
				return false;
			var implicitOperator = varValue.VariableType.GetImplicitOperatorTo(module.ImportReference(("Xamarin.Forms.Core","Xamarin.Forms","BindingBase")), module);
			if (implicitOperator != null)
				return true;

			return varValue.VariableType.InheritsFromOrImplements(module.ImportReference(("Xamarin.Forms.Core", "Xamarin.Forms", "BindingBase")));
		}

		static IEnumerable<Instruction> SetBinding(VariableDefinition parent, FieldReference bpRef, IElementNode elementNode, IXmlLineInfo iXmlLineInfo, ILContext context)
		{
			var module = context.Body.Method.Module;
			var bindableObjectType = ("Xamarin.Forms.Core", "Xamarin.Forms", "BindableObject");
			var parameterTypes = new[] {
				("Xamarin.Forms.Core", "Xamarin.Forms", "BindableProperty"),
				("Xamarin.Forms.Core", "Xamarin.Forms", "BindingBase"),
			};

			//TODO: check if parent is a BP
			foreach (var instruction in parent.LoadAs(module.GetTypeDefinition(bindableObjectType), module))
				yield return instruction;
			yield return Create(Ldsfld, bpRef);
			foreach (var instruction in context.Variables [elementNode].LoadAs(module.GetTypeDefinition(parameterTypes[1]), module))
				yield return instruction;
			yield return Create(Callvirt, module.ImportMethodReference(("Xamarin.Forms.Core", "Xamarin.Forms", "BindableObject"),
																	   methodName: "SetBinding",
																	   parameterTypes: parameterTypes));
		}

		static bool CanSetValue(FieldReference bpRef, bool attached, INode node, IXmlLineInfo iXmlLineInfo, ILContext context)
		{
			var module = context.Body.Method.Module;

			if (bpRef == null)
				return false;
				
			if (node is ValueNode valueNode && valueNode.CanConvertValue(context, bpRef))
				return true;

			if (!(node is IElementNode elementNode))
				return false;

			if (!context.Variables.TryGetValue(elementNode, out VariableDefinition varValue))
				return false;

			var bpTypeRef = bpRef.GetBindablePropertyType(iXmlLineInfo, module);
			// If it's an attached BP, there's no second chance to handle IMarkupExtensions, so we try here.
			// Worst case scenario ? InvalidCastException at runtime
			if (attached && varValue.VariableType.FullName == "System.Object") 
				return true;
			var implicitOperator = varValue.VariableType.GetImplicitOperatorTo(bpTypeRef, module);
			if (implicitOperator != null)
				return true;

			//as we're in the SetValue Scenario, we can accept value types, they'll be boxed
			if (varValue.VariableType.IsValueType && bpTypeRef.FullName == "System.Object")
				return true;

			return varValue.VariableType.InheritsFromOrImplements(bpTypeRef);
		}

		static bool CanGetValue(VariableDefinition parent, FieldReference bpRef, bool attached, IXmlLineInfo iXmlLineInfo, ILContext context, out TypeReference propertyType)
		{
			var module = context.Body.Method.Module;
			propertyType = null;

			if (bpRef == null)
				return false;

			if (!parent.VariableType.InheritsFromOrImplements(module.ImportReference(("Xamarin.Forms.Core", "Xamarin.Forms", "BindableObject"))))
				return false;

			propertyType = bpRef.GetBindablePropertyType(iXmlLineInfo, module);
			return true;
		}

		static IEnumerable<Instruction> SetValue(VariableDefinition parent, FieldReference bpRef, INode node, IXmlLineInfo iXmlLineInfo, ILContext context)
		{
			var valueNode = node as ValueNode;
			var elementNode = node as IElementNode;
			var module = context.Body.Method.Module;
			var bindableObjectType = ("Xamarin.Forms.Core", "Xamarin.Forms", "BindableObject");

//			IL_0007:  ldloc.0 
//			IL_0008:  ldsfld class [Xamarin.Forms.Core]Xamarin.Forms.BindableProperty [Xamarin.Forms.Core]Xamarin.Forms.Label::TextProperty
//			IL_000d:  ldstr "foo"
//			IL_0012:  callvirt instance void class [Xamarin.Forms.Core]Xamarin.Forms.BindableObject::SetValue(class [Xamarin.Forms.Core]Xamarin.Forms.BindableProperty, object)

			foreach (var instruction in parent.LoadAs(module.GetTypeDefinition(bindableObjectType), module))
				yield return instruction;

			yield return Create(Ldsfld, bpRef);

			if (valueNode != null) {
				foreach (var instruction in valueNode.PushConvertedValue(context, bpRef, valueNode.PushServiceProvider(context, bpRef:bpRef), true, false))
					yield return instruction;
			} else if (elementNode != null) {
				var bpTypeRef = bpRef.GetBindablePropertyType(iXmlLineInfo, module);
				foreach (var instruction in context.Variables[elementNode].LoadAs(bpTypeRef, module))
					yield return instruction;
				if (bpTypeRef.IsValueType)
					yield return Create(Box, module.ImportReference(bpTypeRef));
			}
			yield return Create(Callvirt, module.ImportMethodReference(bindableObjectType,
																	   methodName: "SetValue",
																	   parameterTypes: new[] {
																		   ("Xamarin.Forms.Core", "Xamarin.Forms", "BindableProperty"),
																		   ("mscorlib", "System", "Object"),
																	   }));
		}

		static IEnumerable<Instruction> GetValue(VariableDefinition parent, FieldReference bpRef, IXmlLineInfo iXmlLineInfo, ILContext context, out TypeReference propertyType)
		{
			propertyType = bpRef.GetBindablePropertyType(iXmlLineInfo, context.Body.Method.Module);
			return GetValue(parent, bpRef, iXmlLineInfo, context);
		}

		static IEnumerable<Instruction> GetValue(VariableDefinition parent, FieldReference bpRef, IXmlLineInfo iXmlLineInfo, ILContext context)
		{
			var module = context.Body.Method.Module;
			var bindableObjectType = ("Xamarin.Forms.Core", "Xamarin.Forms", "BindableObject");

			foreach (var instruction in parent.LoadAs(module.GetTypeDefinition(bindableObjectType), module))
				yield return instruction;

			yield return Create(Ldsfld, bpRef);
			yield return Create(Callvirt,  module.ImportMethodReference(bindableObjectType,
																		methodName: "GetValue",
																		parameterTypes: new[] { ("Xamarin.Forms.Core", "Xamarin.Forms", "BindableProperty")}));
		}

		static bool CanSet(VariableDefinition parent, string localName, INode node, ILContext context)
		{
			var module = context.Body.Method.Module;
			TypeReference declaringTypeReference;
			var property = parent.VariableType.GetProperty(pd => pd.Name == localName, out declaringTypeReference);
			if (property == null)
				return false;
			var propertyType = property.PropertyType.ResolveGenericParameters(declaringTypeReference);
			var propertySetter = property.SetMethod;
			if (propertySetter == null || !propertySetter.IsPublic || propertySetter.IsStatic)
				return false;

			var valueNode = node as ValueNode;
			if (valueNode != null && valueNode.CanConvertValue(context, propertyType, new ICustomAttributeProvider[] { property, propertyType.ResolveCached()}))
				return true;

			var elementNode = node as IElementNode;
			if (elementNode == null)
				return false;

			var vardef = context.Variables [elementNode];
			var implicitOperator = vardef.VariableType.GetImplicitOperatorTo(propertyType, module);

			if (vardef.VariableType.InheritsFromOrImplements(propertyType))
				return true;
			if (implicitOperator != null)
				return true;
			if (propertyType.FullName == "System.Object")
				return true;

			//I'd like to get rid of this condition. This comment used to be //TODO replace latest check by a runtime type check
			if (vardef.VariableType.FullName == "System.Object")
				return true;

			return false;
		}

		static bool CanGet(VariableDefinition parent, string localName, ILContext context, out TypeReference propertyType)
		{
			var module = context.Body.Method.Module;
			propertyType = null;
			TypeReference declaringTypeReference;
			var property = parent.VariableType.GetProperty(pd => pd.Name == localName, out declaringTypeReference);
			if (property == null)
				return false;
			var propertyGetter = property.GetMethod;
			if (propertyGetter == null || !propertyGetter.IsPublic || propertyGetter.IsStatic)
				return false;

			module.ImportReference(parent.VariableType.ResolveCached());
			var propertyGetterRef = module.ImportReference(module.ImportReference(propertyGetter).ResolveGenericParameters(declaringTypeReference, module));
			propertyGetterRef.ImportTypes(module);
			propertyType = propertyGetterRef.ReturnType.ResolveGenericParameters(declaringTypeReference);

			return true;
		}

		static IEnumerable<Instruction> Set(VariableDefinition parent, string localName, INode node, IXmlLineInfo iXmlLineInfo, ILContext context)
		{
			var module = context.Body.Method.Module;
			TypeReference declaringTypeReference;
			var property = parent.VariableType.GetProperty(pd => pd.Name == localName, out declaringTypeReference);
			var propertySetter = property.SetMethod;

//			IL_0007:  ldloc.0
//			IL_0008:  ldstr "foo"
//			IL_000d:  callvirt instance void class [Xamarin.Forms.Core]Xamarin.Forms.Label::set_Text(string)

			module.ImportReference(parent.VariableType.ResolveCached());
			var propertySetterRef = module.ImportReference(module.ImportReference(propertySetter).ResolveGenericParameters(declaringTypeReference, module));
			propertySetterRef.ImportTypes(module);
			var propertyType = property.PropertyType.ResolveGenericParameters(declaringTypeReference);
			var valueNode = node as ValueNode;
			var elementNode = node as IElementNode;

			//if it's a value type, load the address so we can invoke methods on it
			if (parent.VariableType.IsValueType)
				yield return Instruction.Create(OpCodes.Ldloca, parent);
			else
				yield return Instruction.Create(OpCodes.Ldloc, parent);

			if (valueNode != null) {
				foreach (var instruction in valueNode.PushConvertedValue(context, propertyType, new ICustomAttributeProvider [] { property, propertyType.ResolveCached() }, valueNode.PushServiceProvider(context, propertyRef:property), false, true))
					yield return instruction;
				if (parent.VariableType.IsValueType)
					yield return Instruction.Create(OpCodes.Call, propertySetterRef);
				else
					yield return Instruction.Create(OpCodes.Callvirt, propertySetterRef);
			} else if (elementNode != null) {
				foreach (var instruction in context.Variables [elementNode].LoadAs(propertyType, module))
					yield return instruction;
				if (parent.VariableType.IsValueType)
					yield return Instruction.Create(OpCodes.Call, propertySetterRef);
				else
					yield return Instruction.Create(OpCodes.Callvirt, propertySetterRef);
			}
		}

		static IEnumerable<Instruction> Get(VariableDefinition parent, string localName, IXmlLineInfo iXmlLineInfo, ILContext context, out TypeReference propertyType)
		{
			var module = context.Body.Method.Module;
			var property = parent.VariableType.GetProperty(pd => pd.Name == localName, out var declaringTypeReference);
			var propertyGetter = property.GetMethod;

			module.ImportReference(parent.VariableType.ResolveCached());
			var propertyGetterRef = module.ImportReference(module.ImportReference(propertyGetter).ResolveGenericParameters(declaringTypeReference, module));
			propertyGetterRef.ImportTypes(module);
			propertyType = propertyGetterRef.ReturnType.ResolveGenericParameters(declaringTypeReference);

			if (parent.VariableType.IsValueType)
				return new[] {
					Instruction.Create(OpCodes.Ldloca, parent),
					Instruction.Create(OpCodes.Call, propertyGetterRef),
				};
			else
				return new[] {
					Instruction.Create(OpCodes.Ldloc, parent),
					Instruction.Create(OpCodes.Callvirt, propertyGetterRef),
				};
		}

		static bool CanAdd(VariableDefinition parent, XmlName propertyName, INode node, IXmlLineInfo lineInfo, ILContext context)
		{
			var module = context.Body.Method.Module;
			var localName = propertyName.LocalName;
			bool attached;
			var bpRef = GetBindablePropertyReference(parent, propertyName.NamespaceURI, ref localName, out attached, context, lineInfo);
			TypeReference propertyType;

			if (   !CanGetValue(parent, bpRef, attached, null, context, out propertyType)
				&& !CanGet(parent, localName, context, out propertyType))
				return false;

			//TODO check md.Parameters[0] type
			var adderTuple = propertyType.GetMethods(md => md.Name == "Add" && md.Parameters.Count == 1, module).FirstOrDefault();
			if (adderTuple == null)
				return false;

			return true;
		}

		static Dictionary<VariableDefinition, IList<string>> resourceNamesInUse = new Dictionary<VariableDefinition, IList<string>>();
		static bool CanAddToResourceDictionary(VariableDefinition parent, TypeReference collectionType, IElementNode node, IXmlLineInfo lineInfo, ILContext context)
		{
			if (   collectionType.FullName != "Xamarin.Forms.ResourceDictionary"
				&& collectionType.ResolveCached().BaseType?.FullName != "Xamarin.Forms.ResourceDictionary")
				return false;


			if (node.Properties.ContainsKey(XmlName.xKey)) {
				var key = (node.Properties[XmlName.xKey] as ValueNode).Value as string;
				if (!resourceNamesInUse.TryGetValue(parent, out var names))
					resourceNamesInUse[parent] = (names = new List<string>());
				if (names.Contains(key))
					throw new XamlParseException($"A resource with the key '{key}' is already present in the ResourceDictionary.", lineInfo);
				names.Add(key);
				return true;
			}

			//is there a RD.Add() overrides that accepts this ?
			var nodeTypeRef = context.Variables[node].VariableType;
			var module = context.Body.Method.Module;
			if (module.ImportMethodReference(("Xamarin.Forms.Core", "Xamarin.Forms", "ResourceDictionary"),
											 methodName: "Add",
											 parameterTypes: new[] { (nodeTypeRef.Scope.Name, nodeTypeRef.Namespace, nodeTypeRef.Name) }) != null)
				return true;

			throw new XamlParseException("resources in ResourceDictionary require a x:Key attribute", lineInfo);
		}

		static IEnumerable<Instruction> Add(VariableDefinition parent, XmlName propertyName, INode node, IXmlLineInfo iXmlLineInfo, ILContext context)
		{
			var module = context.Body.Method.Module;
			var elementNode = node as IElementNode;
			var vardef = context.Variables [elementNode];

			TypeReference propertyType;
			foreach (var instruction in GetPropertyValue(parent, propertyName, context, iXmlLineInfo, out propertyType))
				yield return instruction;

			if (CanAddToResourceDictionary(parent, propertyType, elementNode, iXmlLineInfo, context)) {
				foreach (var instruction in AddToResourceDictionary(elementNode, iXmlLineInfo, context))
					yield return instruction;
				yield break;
			}

			var adderTuple = propertyType.GetMethods(md => md.Name == "Add" && md.Parameters.Count == 1, module).FirstOrDefault();
			var adderRef = module.ImportReference(adderTuple.Item1);
			adderRef = module.ImportReference(adderRef.ResolveGenericParameters(adderTuple.Item2, module));

			foreach (var instruction in vardef.LoadAs(adderRef.Parameters[0].ParameterType.ResolveGenericParameters(adderRef), module))
				yield return instruction;
			yield return Instruction.Create(OpCodes.Callvirt, adderRef);
			if (adderRef.ReturnType.FullName != "System.Void")
				yield return Instruction.Create(OpCodes.Pop);
		}

		static IEnumerable<Instruction> AddToResourceDictionary(IElementNode node, IXmlLineInfo lineInfo, ILContext context)
		{
			var module = context.Body.Method.Module;

			if (node.Properties.ContainsKey(XmlName.xKey)) {
//				IL_0014:  ldstr "key"
//				IL_0019:  ldstr "foo"
//				IL_001e:  callvirt instance void class [Xamarin.Forms.Core]Xamarin.Forms.ResourceDictionary::Add(string, object)
				yield return Create(Ldstr, (node.Properties[XmlName.xKey] as ValueNode).Value as string);
				foreach (var instruction in context.Variables[node].LoadAs(module.TypeSystem.Object, module))
					yield return instruction;
				yield return Create(Callvirt, module.ImportMethodReference(("Xamarin.Forms.Core", "Xamarin.Forms", "ResourceDictionary"),
																		   methodName: "Add",
																		   parameterTypes: new[] {
																			   ("mscorlib", "System", "String"),
																			   ("mscorlib", "System", "Object"),
																		   }));
				yield break;
			}

			var nodeTypeRef = context.Variables[node].VariableType;
			yield return Create(Ldloc, context.Variables[node]);
			yield return Create(Callvirt, module.ImportMethodReference(("Xamarin.Forms.Core", "Xamarin.Forms", "ResourceDictionary"),
																	   methodName: "Add",
																	   parameterTypes: new[] { (nodeTypeRef.Scope.Name, nodeTypeRef.Namespace, nodeTypeRef.Name) }));
			yield break;
		}

		static bool GetNameAndTypeRef(ref TypeReference elementType, string namespaceURI, ref string localname,
			ILContext context, IXmlLineInfo lineInfo)
		{
			var dotIdx = localname.IndexOf('.');
			if (dotIdx > 0)
			{
				var typename = localname.Substring(0, dotIdx);
				localname = localname.Substring(dotIdx + 1);
				elementType = new XmlType(namespaceURI, typename, null).GetTypeReference(context.Body.Method.Module, lineInfo);
				return true;
			}
			return false;
		}

		static void SetDataTemplate(IElementNode parentNode, ElementNode node, ILContext parentContext,
			IXmlLineInfo xmlLineInfo)
		{
			var module = parentContext.Module;
			var dataTemplateType = ("Xamarin.Forms.Core", "Xamarin.Forms.Internals", "IDataTemplate");
			var parentVar = parentContext.Variables[parentNode];
			//Push the DataTemplate to the stack, for setting the template
			parentContext.IL.Append(parentVar.LoadAs(module.GetTypeDefinition(dataTemplateType), module));

			//Create nested class
			//			.class nested private auto ansi sealed beforefieldinit '<Main>c__AnonStorey0'
			//			extends [mscorlib]System.Object


			var anonType = new TypeDefinition(
				null,
				"<" + parentContext.Body.Method.Name + ">_anonXamlCDataTemplate_" + dtcount++,
				TypeAttributes.BeforeFieldInit |
				TypeAttributes.Sealed |
				TypeAttributes.NestedPrivate) {
				BaseType = module.TypeSystem.Object,
				CustomAttributes = {
					new CustomAttribute (module.ImportCtorReference(("mscorlib", "System.Runtime.CompilerServices", "CompilerGeneratedAttribute"), parameterTypes: null)),
				}
			};

			parentContext.Body.Method.DeclaringType.NestedTypes.Add(anonType);
			var ctor = anonType.AddDefaultConstructor();

			var loadTemplate = new MethodDefinition("LoadDataTemplate",
				MethodAttributes.Assembly | MethodAttributes.HideBySig,
				module.TypeSystem.Object);
			loadTemplate.Body.InitLocals = true;
			anonType.Methods.Add(loadTemplate);

			var parentValues = new FieldDefinition("parentValues", FieldAttributes.Assembly, module.ImportArrayReference(("mscorlib", "System", "Object")));
			anonType.Fields.Add(parentValues);

			TypeReference rootType = null;
			var vdefRoot = parentContext.Root as VariableDefinition;
			if (vdefRoot != null)
				rootType = vdefRoot.VariableType;
			var fdefRoot = parentContext.Root as FieldDefinition;
			if (fdefRoot != null)
				rootType = fdefRoot.FieldType;

			var root = new FieldDefinition("root", FieldAttributes.Assembly, rootType);
			anonType.Fields.Add(root);

			//Fill the loadTemplate Body
			var templateIl = loadTemplate.Body.GetILProcessor();
			templateIl.Emit(OpCodes.Nop);
			var templateContext = new ILContext(templateIl, loadTemplate.Body, module, parentValues)
			{
				Root = root
			};
			node.Accept(new CreateObjectVisitor(templateContext), null);
			node.Accept(new SetNamescopesAndRegisterNamesVisitor(templateContext), null);
			node.Accept(new SetFieldVisitor(templateContext), null);
			node.Accept(new SetResourcesVisitor(templateContext), null);
			node.Accept(new SetPropertiesVisitor(templateContext, stopOnResourceDictionary: true), null);

			templateIl.Append(templateContext.Variables[node].LoadAs(module.TypeSystem.Object, module));
			templateIl.Emit(OpCodes.Ret);

			//Instanciate nested class
			var parentIl = parentContext.IL;
			parentIl.Emit(OpCodes.Newobj, ctor);

			//Copy required local vars
			parentIl.Emit(OpCodes.Dup); //Duplicate the nestedclass instance
			parentIl.Append(node.PushParentObjectsArray(parentContext));
			parentIl.Emit(OpCodes.Stfld, parentValues);
			parentIl.Emit(OpCodes.Dup); //Duplicate the nestedclass instance
			if (parentContext.Root is VariableDefinition)
				parentIl.Append((parentContext.Root as VariableDefinition).LoadAs(module.TypeSystem.Object, module));
			else if (parentContext.Root is FieldDefinition)
			{
				parentIl.Emit(OpCodes.Ldarg_0);
				parentIl.Emit(OpCodes.Ldfld, parentContext.Root as FieldDefinition);
			}
			else
				throw new InvalidProgramException();
			parentIl.Emit(OpCodes.Stfld, root);

			//SetDataTemplate
			parentIl.Emit(Ldftn, loadTemplate);
			parentIl.Emit(Newobj, module.ImportCtorReference(("mscorlib", "System", "Func`1"),
															 classArguments: new[] { ("mscorlib", "System", "Object") },
															 paramCount: 2));

			parentContext.IL.Emit(OpCodes.Callvirt, module.ImportPropertySetterReference(dataTemplateType, propertyName: "LoadTemplate"));

			loadTemplate.Body.Optimize();
		}

		bool TrySetRuntimeName(XmlName propertyName, VariableDefinition variableDefinition, ValueNode node)
		{
			if (propertyName != XmlName.xName)
				return false;

			var attributes = variableDefinition.VariableType.ResolveCached()
				.CustomAttributes.Where(attribute => attribute.AttributeType.FullName == "Xamarin.Forms.Xaml.RuntimeNamePropertyAttribute").ToList();

			if (!attributes.Any())
				return false;

			var runTimeName = attributes[0].ConstructorArguments[0].Value as string;

			if (string.IsNullOrEmpty(runTimeName)) 
				return false;

			Context.IL.Append(SetPropertyValue(variableDefinition, new XmlName("", runTimeName), node, Context, node));
			return true;
		}
	}

	class VariableDefinitionReference
	{
		public VariableDefinitionReference(VariableDefinition vardef)
		{
			VariableDefinition = vardef;
		}

		public VariableDefinition VariableDefinition { get; set; }

		public static implicit operator VariableDefinition(VariableDefinitionReference vardefref)
		{
			return vardefref.VariableDefinition;
		}
	}
}
