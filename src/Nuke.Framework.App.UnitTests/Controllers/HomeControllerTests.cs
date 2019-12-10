using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nuke.Framework.App.Controllers;

namespace Nuke.Framework.App.UnitTest.Controllers
{
	[TestClass]
	public class HomeControllerTests
	{
		private HomeController CreateHomeController()
		{
			return new HomeController();
		}

		[TestMethod]
		public void Index_StateUnderTest_ExpectedBehavior()
		{
			// Arrange
			var homeController = this.CreateHomeController();

			// Act
			var result = homeController.Index();

			// Assert
			Assert.IsNotNull(result);
		}

		[TestMethod]
		public void About_StateUnderTest_ExpectedBehavior()
		{
			// Arrange
			var homeController = this.CreateHomeController();

			// Act
			var result = homeController.About();

			// Assert
			Assert.IsNotNull(result);
		}

		[TestMethod]
		public void Contact_StateUnderTest_ExpectedBehavior()
		{
			// Arrange
			var homeController = this.CreateHomeController();

			// Act
			var result = homeController.Contact();

			// Assert
			Assert.IsNotNull(result);
		}
	}
}
