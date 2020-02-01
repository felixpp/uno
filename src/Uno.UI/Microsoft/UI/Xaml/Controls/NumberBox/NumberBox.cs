using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Uno.UI.Helpers.WinUI;
using Windows.Globalization.NumberFormatting;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.Foundation.Metadata;
using System.Globalization;
using Uno.Disposables;

namespace Microsoft.UI.Xaml.Controls
{
	public partial class NumberBox : Control
	{
		private bool m_valueUpdating = false;
		private bool m_textUpdating = false;

		private SignificantDigitsNumberRounder m_displayRounder = new SignificantDigitsNumberRounder();

		private TextBox m_textBox;
		private Windows.UI.Xaml.Controls.Primitives.Popup m_popup;

		private SerialDisposable _eventSubscriptions = new SerialDisposable();

		private static string c_numberBoxDownButtonName = "DownSpinButton";
		private static string c_numberBoxUpButtonName = "UpSpinButton";

		private static string c_numberBoxTextBoxName = "InputBox";
		// UNO TODO static string c_numberBoxPopupButtonName= "PopupButton";
		private static string c_numberBoxPopupName = "UpDownPopup";
		private static string c_numberBoxPopupDownButtonName = "PopupDownSpinButton";

		private static string c_numberBoxPopupUpButtonName = "PopupUpSpinButton";
		// UNO TODO static string c_numberBoxPopupContentRootName= "PopupContentRoot";

		// UNO TODO static double c_popupShadowDepth = 16.0;
		// UNO TODO static string c_numberBoxPopupShadowDepthName= "NumberBoxPopupShadowDepth";

		// Shockingly, there is no standard function for trimming strings.
		private const string c_whitespace = " \n\r\t\f\v";

		private static string trim(string s)
		{
			IEnumerable<(char c, int i)> GetNonWhiteSpace()
				=> s.Select((c, i) => (c, i)).Where(p => !c_whitespace.Contains(p.c.ToString()));

			var start = GetNonWhiteSpace().FirstOrDefault();
			var end = GetNonWhiteSpace().LastOrDefault();
			return (start.c == '\0' || end.c == '\0') ? "" : s.Substring(start.i, end.i - start.i + 1);
		}

		public NumberBox()
		{
			// Default values for the number formatter
			var formatter = new DecimalFormatter();
			formatter.IntegerDigits = 1;
			formatter.FractionDigits = 0;
			NumberFormatter = formatter;

			PointerWheelChanged += OnNumberBoxScroll;

			GotFocus += OnNumberBoxGotFocus;
			LostFocus += OnNumberBoxLostFocus;

			Loaded += (s, e) => InitializeTemplate();
			Unloaded += (s, e) => DisposeRegistrations();

			DefaultStyleKey = this;
		}

		protected override AutomationPeer OnCreateAutomationPeer()
		{
			return new NumberBoxAutomationPeer(this);
		}

		protected override void OnApplyTemplate()
		{
			InitializeTemplate();
		}

		private void InitializeTemplate()
		{
			_eventSubscriptions.Disposable = null;

			var registrations = new CompositeDisposable();

			var spinDownName = ResourceAccessor.GetLocalizedStringResource("NumberBoxDownSpinButtonName");
			var spinUpName = ResourceAccessor.GetLocalizedStringResource("NumberBoxUpSpinButtonName");

			if (this.GetTemplateChild(c_numberBoxDownButtonName) is RepeatButton spinDown)
			{
				spinDown.Click += OnSpinDownClick;
				registrations.Add(() => spinDown.Click -= OnSpinDownClick);

				// Do localization for the down button
				if (string.IsNullOrEmpty(AutomationProperties.GetName(spinDown)))
				{
					AutomationProperties.SetName(spinDown, spinDownName);
				}
			}

			if (GetTemplateChild(c_numberBoxUpButtonName) is RepeatButton spinUp)
			{
				spinUp.Click += OnSpinUpClick;
				registrations.Add(() => spinUp.Click -= OnSpinUpClick);

				// Do localization for the up button
				if (string.IsNullOrEmpty(AutomationProperties.GetName(spinUp)))
				{
					AutomationProperties.SetName(spinUp, spinUpName);
				}
			}

			if (GetTemplateChild(c_numberBoxTextBoxName) is TextBox textBox)
			{
				textBox.KeyDown += OnNumberBoxKeyDown;
				registrations.Add(() => textBox.KeyDown -= OnNumberBoxKeyDown);
				textBox.KeyUp += OnNumberBoxKeyUp;
				registrations.Add(() => textBox.KeyUp -= OnNumberBoxKeyUp);

				m_textBox = textBox;
			}

			m_popup = GetTemplateChild(c_numberBoxPopupName) as Windows.UI.Xaml.Controls.Primitives.Popup;

			if (SharedHelpers.IsThemeShadowAvailable())
			{
				// UNO TODO
				//if (GetTemplateChildT(c_numberBoxPopupContentRootName) is UIElement popupRoot)
				//{
				//	if (!popupRoot.Shadow())
				//	{
				//		popupRoot.Shadow(ThemeShadow{});
				//		auto&& translation = popupRoot.Translation();

				//		const double shadowDepth = unbox_value<double>(SharedHelpers.FindResource(c_numberBoxPopupShadowDepthName, Application.Current().Resources(), box_value(c_popupShadowDepth)));

				//		popupRoot.Translation({ translation.x, translation.y, (float)shadowDepth });
				//	}
				//}
			}

			if (GetTemplateChild(c_numberBoxPopupDownButtonName) is RepeatButton popupSpinDown)
			{
				popupSpinDown.Click += OnSpinDownClick;
				registrations.Add(() => popupSpinDown.Click -= OnSpinDownClick);
			}

			if (GetTemplateChild(c_numberBoxPopupUpButtonName) is RepeatButton popupSpinUp)
			{
				popupSpinUp.Click += OnSpinUpClick;
				registrations.Add(() => popupSpinUp.Click -= OnSpinUpClick);
			}

			// .NET rounds to 12 significant digits when displaying doubles, so we will do the same.
			m_displayRounder.SignificantDigits = 12;

			UpdateSpinButtonPlacement();
			UpdateSpinButtonEnabled();

			if (ReadLocalValue(ValueProperty) == DependencyProperty.UnsetValue
				&& ReadLocalValue(TextProperty) != DependencyProperty.UnsetValue)
			{
				// If Text has been set, but Value hasn't, update Value based on Text.
				UpdateValueToText();
			}
			else
			{
				UpdateTextToValue();
			}

			_eventSubscriptions.Disposable = registrations;
		}

		private void DisposeRegistrations()
		{
			_eventSubscriptions.Disposable = null;
		}

		private void OnValuePropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			// This handler may change Value; don't send extra events in that case.
			if (!m_valueUpdating)
			{
				var oldValue = (double)args.OldValue;

				try
				{
					m_valueUpdating = true;

					CoerceValue();

					var newValue = (double)Value;
					if (newValue != oldValue && !(double.IsNaN(newValue) && double.IsNaN(oldValue)))
					{
						// Fire ValueChanged event
						var valueChangedArgs = new NumberBoxValueChangedEventArgs(oldValue, newValue);
						ValueChanged?.Invoke(this, valueChangedArgs);

						// Fire value property change for UIA
						if (FrameworkElementAutomationPeer.FromElement(this) is NumberBoxAutomationPeer peer)
						{
							peer.RaiseValueChangedEvent(oldValue, newValue);
						}
					}

					UpdateTextToValue();
					UpdateSpinButtonEnabled();
				}
				finally
				{
					m_valueUpdating = false;
				}
			}
		}

		private void OnMinimumPropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			CoerceMaximum();
			CoerceValue();

			UpdateSpinButtonEnabled();
		}

		private void OnMaximumPropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			CoerceMinimum();
			CoerceValue();

			UpdateSpinButtonEnabled();
		}

		private void OnSmallChangePropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			UpdateSpinButtonEnabled();
		}

		private void OnIsWrapEnabledPropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			UpdateSpinButtonEnabled();
		}

		private void OnNumberFormatterPropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			// Update text with new formatting
			UpdateTextToValue();
		}

		private void ValidateNumberFormatter(INumberFormatter2 value)
		{
			// NumberFormatter also needs to be an INumberParser
			if (!(value is INumberParser))
			{
				throw new ArgumentException(nameof(value));
			}
		}

		private void OnSpinButtonPlacementModePropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			UpdateSpinButtonPlacement();
		}

		private void OnTextPropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			if (!m_textUpdating)
			{
				UpdateValueToText();
			}
		}

		private void UpdateValueToText()
		{
			if (m_textBox != null)
			{
				m_textBox.Text = Text;
				ValidateInput();
			}
		}

		private void OnValidationModePropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			ValidateInput();
			UpdateSpinButtonEnabled();
		}

		private void OnNumberBoxGotFocus(object sender, RoutedEventArgs args)
		{
			// When the control receives focus, select the text
			if (m_textBox != null)
			{
				m_textBox.SelectAll();
			}

			if (SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Compact)
			{
				if (m_popup != null)
				{
					m_popup.IsOpen = true;
				}
			}
		}

		private void OnNumberBoxLostFocus(object sender, RoutedEventArgs args)
		{
			ValidateInput();

			if (m_popup != null)
			{
				m_popup.IsOpen = false;
			}
		}

		private void CoerceMinimum()
		{
			var max = Maximum;
			if (Minimum > max)
			{
				Minimum = max;
			}
		}

		private void CoerceMaximum()
		{
			var min = Minimum;
			if (Maximum < min)
			{
				Maximum = min;
			}
		}

		private void CoerceValue()
		{
			// Validate that the value is in bounds
			var value = Value;
			if (!double.IsNaN(value) && !IsInBounds(value) && ValidationMode == NumberBoxValidationMode.InvalidInputOverwritten)
			{
				// Coerce value to be within range
				var max = Maximum;
				if (value > max)
				{
					Value = max;
				}
				else
				{
					Value = Minimum;
				}
			}
		}

		private void ValidateInput()
		{
			// Validate the content of the inner textbox
			if (m_textBox != null)
			{
				var text = trim(m_textBox.Text);

				// Handles empty TextBox case, set text to current value
				if (string.IsNullOrEmpty(text))
				{
					Value = double.NaN;
				}
				else
				{
					// Setting NumberFormatter to something that isn't an INumberParser will throw an exception, so this should be safe
					var numberParser = NumberFormatter as INumberParser;

					var value = AcceptsExpression
						? NumberBoxParser.Compute(text, numberParser)
						: ApiInformation.IsTypePresent(numberParser?.GetType().FullName)
							? numberParser.ParseDouble(text)
							: double.TryParse(text, out var v)
								? (double?)v
								: null;

					if (value == null)
					{
						if (ValidationMode == NumberBoxValidationMode.InvalidInputOverwritten)
						{
							// Override text to current value
							UpdateTextToValue();
						}
					}
					else
					{
						if (value.Value == Value)
						{
							// Even if the value hasn't changed, we still want to update the text (e.g. Value is 3, user types 1 + 2, we want to replace the text with 3)
							UpdateTextToValue();
						}
						else
						{
							Value = value.Value;
						}
					}
				}
			}
		}

		private void OnSpinDownClick(object sender, RoutedEventArgs args)
		{
			StepValue(-SmallChange);
		}

		private void OnSpinUpClick(object sender, RoutedEventArgs args)
		{
			StepValue(SmallChange);
		}

		private void OnNumberBoxKeyDown(object sender, KeyRoutedEventArgs args)
		{
			// Handle these on key down so that we get repeat behavior.
			switch (args.OriginalKey)
			{
				case VirtualKey.Up:
					StepValue(SmallChange);
					args.Handled = true;
					break;

				case VirtualKey.Down:
					StepValue(-SmallChange);
					args.Handled = true;
					break;

				case VirtualKey.PageUp:
					StepValue(LargeChange);
					args.Handled = true;
					break;

				case VirtualKey.PageDown:
					StepValue(-LargeChange);
					args.Handled = true;
					break;
			}
		}

		private void OnNumberBoxKeyUp(object sender, KeyRoutedEventArgs args)
		{
			switch (args.OriginalKey)
			{
				case VirtualKey.Enter:
				case VirtualKey.GamepadA:
					ValidateInput();
					args.Handled = true;
					break;

				case VirtualKey.Escape:
				case VirtualKey.GamepadB:
					UpdateTextToValue();
					args.Handled = true;
					break;
			}
		}

		private void OnNumberBoxScroll(object sender, PointerRoutedEventArgs args)
		{
			if (m_textBox != null)
			{
				if (m_textBox.FocusState != FocusState.Unfocused)
				{
					var delta = args.GetCurrentPoint(this).Properties.MouseWheelDelta;
					if (delta > 0)
					{
						StepValue(SmallChange);
					}
					else if (delta < 0)
					{
						StepValue(-SmallChange);
					}
				}
			}
		}

		private void StepValue(double change)
		{
			// Before adjusting the value, validate the contents of the textbox so we don't override it.
			ValidateInput();

			var newVal = Value;
			if (!double.IsNaN(newVal))
			{
				newVal += change;

				if (IsWrapEnabled)
				{
					var max = Maximum;
					var min = Minimum;

					if (newVal > max)
					{
						newVal = min;
					}
					else if (newVal < min)
					{
						newVal = max;
					}
				}

				Value = newVal;
			}
		}

		// Updates TextBox.Text with the formatted Value
		private void UpdateTextToValue()
		{
			if (m_textBox != null)
			{
				string newText = "";

				var value = Value;
				if (!double.IsNaN(value))
				{
					// Rounding the value here will prevent displaying digits caused by floating point imprecision.
					var roundedValue = m_displayRounder.RoundDouble(value);

					if (ApiInformation.IsTypePresent(NumberFormatter.GetType().FullName))
					{
						newText = NumberFormatter.FormatDouble(roundedValue);
					}
					else
					{
						newText = roundedValue.ToString($"0." + new string('#', (int)m_displayRounder.SignificantDigits), CultureInfo.CurrentCulture);
					}
				}

				m_textBox.Text = newText;

				try
				{
					m_textUpdating = true;
					Text = newText;

					// This places the caret at the end of the text.
					m_textBox.Select(newText.Length, 0);
				}
				finally
				{
					m_textUpdating = false;
				}
			}
		}

		private void UpdateSpinButtonPlacement()
		{
			var spinButtonMode = SpinButtonPlacementMode;

			if (spinButtonMode == NumberBoxSpinButtonPlacementMode.Inline)
			{
				VisualStateManager.GoToState(this, "SpinButtonsVisible", false);
			}
			else if (spinButtonMode == NumberBoxSpinButtonPlacementMode.Compact)
			{
				VisualStateManager.GoToState(this, "SpinButtonsPopup", false);
			}
			else
			{
				VisualStateManager.GoToState(this, "SpinButtonsCollapsed", false);
			}
		}

		private void UpdateSpinButtonEnabled()
		{
			var value = Value;
			bool isUpButtonEnabled = false;
			bool isDownButtonEnabled = false;

			if (!double.IsNaN(value))
			{
				if (IsWrapEnabled || ValidationMode != NumberBoxValidationMode.InvalidInputOverwritten)
				{
					// If wrapping is enabled, or invalid values are allowed, then the buttons should be enabled
					isUpButtonEnabled = true;
					isDownButtonEnabled = true;
				}
				else
				{
					if (value < Maximum)
					{
						isUpButtonEnabled = true;
					}
					if (value > Minimum)
					{
						isDownButtonEnabled = true;
					}
				}
			}

			VisualStateManager.GoToState(this, isUpButtonEnabled ? "UpSpinButtonEnabled" : "UpSpinButtonDisabled", false);
			VisualStateManager.GoToState(this, isDownButtonEnabled ? "DownSpinButtonEnabled" : "DownSpinButtonDisabled", false);
		}

		private bool IsInBounds(double value)
		{
			return (value >= Minimum && value <= Maximum);
		}
	}
}