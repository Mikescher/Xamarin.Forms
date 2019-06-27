﻿using System;

namespace Xamarin.Forms
{
	public interface IItemsViewSource : IDisposable
	{
		int Count { get; }
		object this[int index] { get; }
	}
}