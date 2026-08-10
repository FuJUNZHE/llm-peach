using System;
using System.Collections.Generic;
using System.IO;
using Peach.Core;
using Peach.Core.IO;
using Peach.Pro.Core.Publishers;

namespace Peach.LLM.Core.Publishers
{
	/// <summary>
	/// Writes every output call to an individual file for the current fuzzing iteration.
	/// </summary>
	[Publisher("FilePerPacket")]
	[Parameter("FileName", typeof(string), "Name template for the files to open for writing")]
	public class FilePerPacketPublisher : FilePublisher
	{
		private readonly string _fileTemplate;
		private uint _packetIndex;

		public FilePerPacketPublisher(Dictionary<string, Variant> args)
			: base(args)
		{
			_fileTemplate = FileName;

			try
			{
				var formattedFileName = string.Format(_fileTemplate, 0);
				if (formattedFileName == _fileTemplate)
					throw new PeachException("Error, FileName \"" + _fileTemplate + "\" missing iteration format identifier.");
			}
			catch (FormatException ex)
			{
				throw new PeachException("Error, FileName \"" + _fileTemplate + "\" is not a valid format string.", ex);
			}
		}

		protected override void OnOpen()
		{
			_packetIndex = 0;
		}

		protected override void OnClose()
		{
			// Each packet stream is opened and closed by OnOutput.
		}

		protected override void OnOutput(BitwiseStream data)
		{
			FileName = GetPacketFileName(_packetIndex);

			try
			{
				base.OnOpen();
				base.OnOutput(data);
			}
			finally
			{
				if (stream != null)
					base.OnClose();
			}

			++_packetIndex;
		}

		private string GetPacketFileName(uint packetIndex)
		{
			var iterationFileName = string.Format(_fileTemplate, Iteration);
			if (IsControlIteration)
				iterationFileName += ".Control";

			var directory = Path.GetDirectoryName(iterationFileName);
			var name = Path.GetFileNameWithoutExtension(iterationFileName);
			var extension = Path.GetExtension(iterationFileName);
			var packetFileName = string.Format("{0}__{1}{2}", name, packetIndex, extension);

			return string.IsNullOrEmpty(directory)
				? packetFileName
				: Path.Combine(directory, packetFileName);
		}
	}
}
