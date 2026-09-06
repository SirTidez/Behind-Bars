(() => {
  const menuButton = document.querySelector("[data-menu-button]");
  const navigation = document.querySelector("[data-site-nav]");

  if (!menuButton || !navigation) {
    return;
  }

  const closeMenu = () => {
    navigation.dataset.open = "false";
    menuButton.setAttribute("aria-expanded", "false");
    menuButton.textContent = "Menu";
  };

  menuButton.addEventListener("click", () => {
    const isOpen = navigation.dataset.open === "true";
    navigation.dataset.open = String(!isOpen);
    menuButton.setAttribute("aria-expanded", String(!isOpen));
    menuButton.textContent = isOpen ? "Menu" : "Close";
  });

  navigation.querySelectorAll("a").forEach((link) => {
    link.addEventListener("click", closeMenu);
  });

  window.addEventListener("resize", () => {
    if (window.innerWidth > 1080) {
      closeMenu();
    }
  });
})();
