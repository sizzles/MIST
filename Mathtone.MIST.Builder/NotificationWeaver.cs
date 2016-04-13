﻿using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Mathtone.MIST {
	/// <summary>
	/// Alters IL assemblies after build and implements a notification mechanism.
	/// </summary>
	public class NotificationWeaver {

		string NotifyTargetName = typeof(NotifyTarget).FullName;
		string NotifierTypeName = typeof(NotifierAttribute).FullName;
		string NotifyTypeName = typeof(NotifyAttribute).FullName;
		string SuppressNotifyTypeName = typeof(SuppressNotifyAttribute).FullName;
		string assemblyPath;
		DefaultAssemblyResolver resolver;
		MetadataResolver mdResolver;

		string ApplicationPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

		/// <summary>
		/// Initializes a new instance of the <see cref="NotificationWeaver"/> class.
		/// </summary>
		/// <param name="assemblyPath">Path to the assembly which is to be altered.</param>
		public NotificationWeaver(string assemblyPath) {

			this.assemblyPath = assemblyPath;
			this.resolver = new DefaultAssemblyResolver();
			this.resolver.AddSearchDirectory(ApplicationPath);
			this.resolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath));
			this.mdResolver = new MetadataResolver(resolver);
		}

		/// <summary>
		/// Weaves the notification mechanism into the assembly
		/// </summary>
		/// <param name="debug">if set to <c>true</c> [debug].</param>
		public void InsertNotifications(bool debug = false) {

			bool mustSave = false;
			var assemblyDef = null as AssemblyDefinition;
			var readParameters = new ReaderParameters { ReadSymbols = debug, AssemblyResolver = resolver };
			var writeParameters = new WriterParameters { WriteSymbols = debug };

			//Load the assembly.
			using (var stream = File.OpenRead(assemblyPath)) {
				assemblyDef = AssemblyDefinition.ReadAssembly(stream, readParameters);
			}

			//Search for types and weave notifiers into them if necessary.
			foreach (var moduleDef in assemblyDef.Modules) {
				foreach (var typeDef in moduleDef.Types) {
					mustSave |= ProcessType(typeDef);
				}
			}

			//If the assembly has been altered then rewrite it.
			if (mustSave) {
				using (var stream = File.OpenWrite(assemblyPath)) {
					assemblyDef.Write(stream, writeParameters);
					stream.Flush();
				}
			}
		}

		/// <summary>
		/// Weaves the notification mechanism into the supplied type.
		/// </summary>
		/// <param name="typeDef">The type definition.</param>
		/// <returns><c>true</c> if the type was altered, <c>false</c> otherwise.</returns>
		/// <exception cref="System.Exception"></exception>
		protected bool ProcessType(TypeDefinition typeDef) {

			var rtn = false;

			//Search for a NotifyAttribute
			var notifierAttr = typeDef.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == NotifierTypeName);

			if (notifierAttr != null) {

				//Use explicit mode if not otherwise specified
				var mode = NotificationMode.Explicit;

				//Locate the notification target method.
				var notifyTarget = GetNotifyTarget(typeDef);

				if (notifyTarget == null) {
					throw new Exception($"Cannot locate notify target for type: {typeDef.Name}");
				}

				//Determine whether to use explicit/implicit notifier identification.
				if (notifierAttr.HasConstructorArguments) {
					mode = (NotificationMode)notifierAttr.ConstructorArguments[0].Value;
				}

				//Identify the name of the property/properties that will be passed to the notification method.
				foreach (var propDef in typeDef.Properties) {
					var propNames = GetNotifyPropertyNames(propDef);

					if (! ContainsAttribute(propDef,SuppressNotifyTypeName)) {
						//In implcit mode implement notification for all public properties
						if (!propNames.Any() && mode == NotificationMode.Implicit && propDef.GetMethod.IsPublic) {
							propNames = new[] { propDef.Name };
						}
						if (propNames != null) {
							InsertNotificationsIntoProperty(propDef, notifyTarget, propNames);
							rtn = true;
						}
					}
				}
			}

			//Recursively process any nested type definitions.
			foreach (var type in typeDef.NestedTypes) {
				ProcessType(type);
			}

			return rtn;
		}

		/// <summary>
		/// Gets the notification target method, market with a <see cref="NotifyTarget"/> attribute.
		/// </summary>
		/// <param name="typeDef">The type definition.</param>
		/// <returns>MethodReference.</returns>
		protected MethodReference GetNotifyTarget(TypeDefinition typeDef) {

			//Check each method for a NotifyTargetAttribute
			foreach (var methDef in typeDef.Methods) {
				if (ContainsAttribute(methDef, NotifyTargetName)) {
					//Verify target method has an appropriate signature
					if (methDef.Parameters.Count == 1) {
						var parameterType = methDef.Parameters[0].ParameterType.FullName;
						if (parameterType == typeof(string).FullName) {
							return methDef;
						}
						else {
							throw new InvalidOperationException($"Notify target {methDef.DeclaringType.FullName}.{methDef.Name} is not an Action<string>");
						}
					}
				}
			}

			//Notify target not found, search base type
			var baseType = typeDef.BaseType;
			if (baseType != null) {
				//Get the definition of the base type
				var baseTypeDef = mdResolver.Resolve(baseType);

				//Search recursively for a target
				var rtn = GetNotifyTarget(baseTypeDef);

				if (rtn != null) {
					//A target has been found, import a reference to the target method;
					rtn = typeDef.Module.ImportReference(rtn);
				}

				return rtn;
			}
			else {
				return null;
			}
		}

		/// <summary>
		/// Determines whether the specified definition is decorated with an attribute of the named type.
		/// </summary>
		/// <param name="definition">The definition.</param>
		/// <param name="attributeTypeName">Name of the attribute type.</param>
		/// <returns><c>true</c> if the specified definition contains attribute; otherwise, <c>false</c>.</returns>
		public static bool ContainsAttribute(MethodDefinition definition, string attributeTypeName) =>
			definition.CustomAttributes.Any(a => a.AttributeType.FullName == attributeTypeName);

		/// <summary>
		/// Determines whether the specified definition is decorated with an attribute of the named type.
		/// </summary>
		/// <param name="definition">The definition.</param>
		/// <param name="attributeTypeName">Name of the attribute type.</param>
		/// <returns><c>true</c> if the specified definition contains attribute; otherwise, <c>false</c>.</returns>
		public static bool ContainsAttribute(PropertyDefinition definition, string attributeTypeName) =>
			definition.CustomAttributes.Any(a => a.AttributeType.FullName == attributeTypeName);

		/// <summary>
		/// Gets the property names that should be passed to the notification target method when the property value is changed.
		/// </summary>
		/// <param name="propDef">The property definition.</param>
		/// <returns>IEnumerable&lt;System.String&gt;.</returns>
		IEnumerable<string> GetNotifyPropertyNames(PropertyDefinition propDef) {
			//Check for the NotifyAttribute
			var attr = propDef.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == NotifyTypeName);

			if (attr != null) {
				//Return property names supplied by the constructor, if none are specified return the property name itself.
				if (attr.HasConstructorArguments) {
					var args = attr.ConstructorArguments[0].Value as CustomAttributeArgument[];
					if (args == null) {
						//Argument is null
						yield return null;
					}
					else if (args.Length == 0) {
						//Apparently the user saw reason to pass an empty array.
						yield return propDef.Name;
					}
					else {
						//Multiple arguments have been passed.
						foreach (var arg in args) {
							yield return (string)arg.Value;
						}
					}
				}
				else {
					//No fancy stuff, just return the property name.
					yield return propDef.Name;
				}
			}
		}

		/// <summary>
		/// Weaves notifiers into the property.  This is where the magic happens.
		/// </summary>
		/// <param name="propDef">The property definition.</param>
		/// <param name="notifyTarget">The notify target.</param>
		/// <param name="notifyPropertyNames">The notify property names.</param>
		protected static void InsertNotificationsIntoProperty(PropertyDefinition propDef, MethodReference notifyTarget, IEnumerable<string> notifyPropertyNames) {

			//Should produce something like the following:
			/*
			.method public hidebysig specialname instance void 
			.set_SomeProperty(string 'value') cil managed
			{
			  .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
			  // Code size       8 (0x8)
			  .maxstack  8
			  IL_0000:  ldarg.0
			  IL_0001:  ldarg.1
			  IL_0002:  stfld      string Mathtone.MIST.Tests.TestNotifier::'<SomeProperty>k__BackingField'
			  IL_0007:  ret
			} // end of method TestNotifier::set_SomeProperty
			*/

			if (propDef.SetMethod == null)
				//This is a read-ony property
				return;
			else if (propDef.SetMethod.Body == null) {
				//This is an abstract property, we don't do these either.
				throw new InvalidOperationException("NotifyAttribute cannot be set on abstract properties");
			}

			//Retrieve an IL writer
			var msil = propDef.SetMethod.Body.GetILProcessor();

			//Insert a Nop before the first instruction (like... at the beginning).
			msil.InsertBefore(propDef.SetMethod.Body.Instructions[0], msil.Create(OpCodes.Nop));

			//Call the notification tareget method for 
			foreach (var notifyPropertyName in notifyPropertyNames) {

				//Load argument 0 onto the stack
				var ldarg0 = msil.Create(OpCodes.Ldarg_0);

				//Emit a call to the 
				var callNotifyTarget = msil.Create(OpCodes.Call, notifyTarget);

				//Load the value of the property name to be passed to the notify target onto the stack.
				var propertyName = notifyPropertyName == null ?
					msil.Create(OpCodes.Ldnull) :
					msil.Create(OpCodes.Ldstr, notifyPropertyName);

				//Begin inserting IL before the last instruction of the set method (presumably a return statement).
				msil.InsertBefore(propDef.SetMethod.Body.Instructions[propDef.SetMethod.Body.Instructions.Count - 1], ldarg0);

				//Insert property name
				msil.InsertAfter(ldarg0, propertyName);

				//Insert call to notify target
				msil.InsertAfter(propertyName, callNotifyTarget);

				//~FIN
				msil.InsertAfter(callNotifyTarget, msil.Create(OpCodes.Nop));
			}
		}
	}
}