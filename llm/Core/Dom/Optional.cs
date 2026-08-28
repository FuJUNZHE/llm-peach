using System;
using System.Collections.Generic;
using System.Xml;
using Peach.Core;
using Peach.Core.Analyzers;
using Peach.Core.Cracker;
using Peach.Core.Dom;
using Peach.Core.IO;
using NLog;

namespace Peach.LLM.Core.Dom
{
	/// <summary>
	/// Optional data element - Conditional element wrapper
	/// Includes or excludes wrapped content based on expression evaluation
	/// src element's value is available as 'value' in the expression
	/// </summary>
	[DataElement("Optional")]
	[PitParsable("Optional")]
	[Parameter("name", typeof(string), "Element name", "")]
	[Parameter("fieldId", typeof(string), "Element field ID", "")]
	[Parameter("src", typeof(string), "Reference to element to use in expression. `src` can be a dot-separated path, or a relative path using '.' and '..' segments (for example, './field' or '../header.type').", "")]
	[Parameter("expression", typeof(string), "Scripting expression in *Python* for conditional inclusion (src value available as 'value'; special characters must be escaped using XML entities.)", "")]
	[Parameter("length", typeof(uint?), "Length in data element", "")]
	[Parameter("lengthType", typeof(LengthType), "Units of the length attribute", "bytes")]
	[Parameter("mutable", typeof(bool), "Is element mutable", "true")]
	[Parameter("minOccurs", typeof(int), "Minimum occurrences", "0")]
	[Parameter("maxOccurs", typeof(int), "Maximum occurrences", "1")]
	[Parameter("occurs", typeof(int), "Actual occurrences", "0")]
	[Serializable]
	public class Optional : Block
	{
		private static readonly NLog.Logger logger = LogManager.GetCurrentClassLogger();

		/// <summary>
		/// Reference to the element that should be optionally included
		/// </summary>
		public string SourcePath { get; set; }

		/// <summary>
		/// Expression that determines if the element should be included
		/// </summary>
		public string Expression { get; set; }

		/// <summary>
		/// Cached reference to the actual element
		/// </summary>
		private DataElement _refElement;

		public bool Exists
		{
			get
			{
				if (_exists.HasValue)
					return _exists.Value;
				try
				{
					return EvaluateCondition();
				} 
				catch (Exception ex)
				{
					logger.Error(ex, "Error evaluating condition for Optional '{0}': {1}", debugName, ex.Message);
					return false; // On error, treat as not existing
				}
			}
			set => _exists = value;
		}

		private bool? _exists;

		private bool _isCracking = true;

		public Optional()
			: base()
		{
			Invalidated += (sender, args) =>
			{
				if (!_isCracking)
				{
					_exists = true;
				}
			};
		}

		public Optional(string name)
			: base(name)
		{
			Invalidated += (sender, args) =>
			{
				if (!_isCracking)
				{
					_exists = true;
				}
			};
		}

		/// <summary>
		/// Get the referenced element
		/// </summary>
		public DataElement GetReferencedElement()
		{
			if (_refElement != null)
				return _refElement;

			if (string.IsNullOrWhiteSpace(SourcePath))
				return null;

			DataElement elem = null;

			if (IsRelativePath(SourcePath))
			{
				elem = ResolveRelativePath(SourcePath);
				_refElement = elem;
				return elem;
			}

			// Prefer the current runtime ancestor chain before falling back to the
			// regular name search.  A Choice candidate is cracked before it becomes
			// Choice.SelectedElement, so walking down from the Choice at that point
			// exposes the template in choiceElements instead of the candidate being
			// cracked.  Matching the path against our ancestors keeps references such
			// as "connect.msg_body.header.flags" on the runtime candidate.
			elem = ResolveAncestorPath(SourcePath);
			if (elem != null)
			{
				_refElement = elem;
				return elem;
			}

			var p = parent;
			while (p != null)
			{
				elem = p.find(SourcePath);

				if (elem != null)
					break;

				p = p.parent;
			}

			_refElement = elem;
			return elem;
		}

		private DataElement ResolveAncestorPath(string path)
		{
			var parts = path.Split(new[] { '.' }, StringSplitOptions.None);
			foreach (var part in parts)
			{
				if (part.Length == 0)
					return null;
			}

			// Stored nearest-first.  For an ancestor at index i, index i - 1 is
			// the next node on the actual runtime path towards this Optional.
			var ancestors = new List<DataElement>();
			for (DataElement current = this; current != null; current = current.parent)
				ancestors.Add(current);

			for (var i = 0; i < ancestors.Count; ++i)
			{
				if (ancestors[i].Name != parts[0])
					continue;

				var anchor = ancestors[i];
				var ancestorIndex = i - 1;
				var partIndex = 1;

				// Consume as much of the path as possible using the actual ancestor
				// chain.  This also lets a root-qualified path cross an in-progress
				// Choice candidate without traversing Choice.Children().
				while (partIndex < parts.Length && ancestorIndex >= 0 &&
					ancestors[ancestorIndex].Name == parts[partIndex])
				{
					anchor = ancestors[ancestorIndex];
					--ancestorIndex;
					++partIndex;
				}

				if (partIndex == parts.Length)
					return anchor;

				var container = anchor as DataElementContainer;
				if (container == null)
					continue;

				var remaining = string.Join(".", parts, partIndex, parts.Length - partIndex);
				var elem = ResolveChildPath(container, remaining);
				if (elem != null)
					return elem;
			}

			return null;
		}

		private static bool IsRelativePath(string path)
		{
			return path == "." || path == ".." ||
				path.StartsWith("./", StringComparison.Ordinal) ||
				path.StartsWith("../", StringComparison.Ordinal);
		}

		private DataElement ResolveRelativePath(string path)
		{
			DataElement current = this;
			var parts = path.Split(new[] { '/' }, StringSplitOptions.None);

			foreach (var part in parts)
			{
				if (part.Length == 0 || part == ".")
					continue;

				if (part == "..")
				{
					current = current.parent;
					if (current == null)
						return null;

					continue;
				}

				var container = current as DataElementContainer;
				if (container == null)
					return null;

				var child = ResolveChildPath(container, part);

				if (child == null)
					return null;

				current = child;
			}

			return current;
		}

		private static DataElement ResolveChildPath(DataElementContainer container, string path)
		{
			DataElement current = container;

			foreach (var name in path.Split(new[] { '.' }, StringSplitOptions.None))
			{
				if (name.Length == 0)
					return null;

				var currentContainer = current as DataElementContainer;
				DataElement child;
				if (currentContainer == null || !currentContainer.TryGetValue(name, out child))
					return null;

				current = child;
			}

			return current;
		}

		/// <summary>
		/// Crack the optional element and its children based on expression condition
		/// Follows the same pattern as DataElementContainer.Crack()
		/// </summary>
		public override void Crack(DataCracker context, BitStream data, long? size)
		{
			BitStream sizedData = ReadSizedData(data, size);
			long startPosition = data.PositionBits;

			try
			{
				if (!Exists)
				{
					logger.Trace("Optional '{0}': Condition `{1}` evaluated to false, skipping cracking", debugName, Expression);
					return;
				}

				logger.Trace("Optional '{0}': Condition `{1}` evaluated to true, proceeding with cracking", debugName, Expression);
				
				// Process children similar to DataElementContainer.Crack
				var prevCount = Count;

				// Handle children, iterate since cracking can modify the list
				for (var i = 0; i < Count; )
				{
					var child = this[i];
					context.CrackData(child, sizedData);

					// If we are unsized, cracking a child can cause our size
					// to be available. If so, update and keep going.
					if (!size.HasValue)
					{
						size = context.GetElementSize(this);

						if (size.HasValue)
						{
							long read = data.PositionBits - startPosition;
							sizedData = ReadSizedData(data, size, read);
						}
					}

					// if Count < prevCount, the child was placed in a different
					// spot in the dom, so don't increment our index
					if (Count == prevCount)
					{
						// if the child's index is previous to our current index
						// the child was placed behind the current position.
						// it will get cracked on a subsequent pass so just
						// increment the index to go to the next element.
						// if the child index is ahead of the index, this means
						// it was placed and not cracked so keep the index the same
						var idx = IndexOf(child);

						System.Diagnostics.Debug.Assert(idx >= 0);

						if (idx <= i)
							++i;
					}
					else if (Count > prevCount)
					{
						// Cracking child caused new elements to be added to
						// this container, so set our next index to be one
						// after the index of the child
						i = IndexOf(child) + 1;
					}

					prevCount = Count;
				}

				if (size.HasValue && sizedData == data)
					data.SeekBits(startPosition + size.Value, System.IO.SeekOrigin.Begin);
			}
			catch (CrackingFailure)
			{
				logger.Trace("Optional '{0}': Cracking failed", debugName);
				throw;
			}
			catch (Exception ex)
			{
				logger.Trace("Optional '{0}': Exception during cracking: {1}", debugName, ex.Message);
				throw new CrackingFailure(string.Format(
					"Optional '{0}' cracking failed: {1}", debugName, ex.Message), this, data, ex);
			}

			_isCracking = false;
		}

		/// <summary>
		/// Evaluate the conditional expression
		/// src element's value is available as 'value' in the expression
		/// </summary>
		public bool EvaluateCondition()
		{
			if (string.IsNullOrWhiteSpace(Expression))
				return true; // No expression means always include

			try
			{
				var refElement = GetReferencedElement();
				if (refElement == null)
					throw new PeachException(string.Format(
						"Optional '{0}': Referenced element '{1}' not found", debugName, SourcePath));

				// Flags are containers and do not have a DefaultValue. Their value is
				// assembled from their child Flag elements in InternalValue.
				var flags = refElement as Flags;
				var refValue = flags == null ? refElement.DefaultValue : GetFlagsValue(flags);
				if (refValue == null)
					throw new PeachException(string.Format(
						"Optional '{0}': Referenced element '{1}' has no value", debugName, SourcePath));

				// Create state dictionary similar to SizeRelation
				var state = new Dictionary<string, object>
				{
					{ "self", this }
				};

                switch (refValue.GetVariantType())
                {
                    case Variant.VariantType.Int:
                        state["value"] = (int)refValue;
                        break;
                    case Variant.VariantType.Long:
                        state["value"] = (long)refValue;
                        break;
                    case Variant.VariantType.ULong:
                        state["value"] = (ulong)refValue;
                        break;
                    case Variant.VariantType.String:
                        state["value"] = (string)refValue;
                        break;
                    case Variant.VariantType.Double:
                        state["value"] = (double)refValue;
                        break;
                    case Variant.VariantType.ByteString:
                        state["value"] = (byte[])refValue;
                        break;
                    case Variant.VariantType.BitStream:
                        state["value"] = (BitwiseStream)refValue;
                        break;
                    default:
                        logger.Warn("Optional '{0}': Unsupported ref value type {1}, expression may not evaluate correctly", debugName, refValue.GetVariantType());
                        state["value"] = refValue;
                        break;
                }

				// Evaluate the expression using DataElement's built-in scripting
				object result = EvalExpression(Expression, state);

                logger.Trace("Optional '{0}': Expression `{1}` evaluated to {2} with value = {3}", debugName, Expression, result, refValue);
				
				// Convert result to bool
				if (result is bool)
					return (bool)result;

				if (result is int)
					return (int)result != 0;

				if (result is long)
					return (long)result != 0;

				if (result is ulong)
					return (ulong)result != 0;

				if (result is string)
					return !string.IsNullOrEmpty((string)result);

				return result != null;
			}
			catch (Exception ex)
			{
				throw new PeachException(string.Format(
					"Error evaluating Optional '{0}' expression '{1}': {2}",
					debugName, Expression, ex.Message), ex);
			}
		}

		private static Variant GetFlagsValue(Flags flags)
		{
			var internalValue = flags.InternalValue;
			if (internalValue == null || internalValue.GetVariantType() != Variant.VariantType.BitStream)
				throw new PeachException(string.Format(
					"Optional: Flags element '{0}' did not produce a bit stream value", flags.debugName));

			var stream = (BitwiseStream)internalValue;
			var position = stream.PositionBits;

			try
			{
				stream.SeekBits(0, System.IO.SeekOrigin.Begin);

				ulong bits;
				var length = (int)flags.lengthAsBits;
				var read = stream.ReadBits(out bits, length);
				if (read != length)
					throw new PeachException(string.Format(
						"Optional: Could not read the complete value of Flags element '{0}'", flags.debugName));

				var endian = flags.LittleEndian ? Endian.Little : Endian.Big;
				return new Variant(endian.GetUInt64(bits, length));
			}
			finally
			{
				stream.PositionBits = position;
			}
		}

		/// <summary>
		/// Parse the Optional element from PIT XML
		/// </summary>
		public static new DataElement PitParser(PitParser context, XmlNode node, DataElementContainer parent)
		{
			if (node.Name != "Optional")
				return null;

            Optional optional;

			optional = Generate<Optional>(node, parent);
			optional.parent = parent;


			// Parse 'expression' attribute
			var exprAttr = node.Attributes["expression"];
			if (exprAttr != null)
				optional.Expression = exprAttr.Value;
			var srcAttr = node.Attributes["src"];
			if (srcAttr != null && string.IsNullOrWhiteSpace(optional.SourcePath))
				optional.SourcePath = srcAttr.Value;

			context.handleCommonDataElementAttributes(node, optional);
			context.handleCommonDataElementChildren(node, optional);
			context.handleDataElementContainer(node, optional);

			return optional;
		}

		/// <summary>
		/// Write the Optional element to PIT XML
		/// </summary>
		public override void WritePit(XmlWriter pit)
		{
			pit.WriteStartElement("Optional");

			if (referenceName != null)
				pit.WriteAttributeString("src", referenceName);

			if (!string.IsNullOrEmpty(Expression))
				pit.WriteAttributeString("expression", Expression);

			WritePitCommonAttributes(pit);
			WritePitCommonChildren(pit);

			foreach (var obj in this)
				obj.WritePit(pit);

			pit.WriteEndElement();
		}

		protected override Variant GenerateDefaultValue()
		{
			if (!Exists)
				return new Variant(new byte[0]);

			return base.GenerateDefaultValue();
		}
	}
}
