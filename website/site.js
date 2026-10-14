const dialog = document.querySelector("#image-dialog");
const closeButton = dialog.querySelector(".dialog-close");
for (const button of document.querySelectorAll("[data-image]")) {
  button.addEventListener("click", () => {
    dialog.querySelector("img").src = button.dataset.image;
    dialog.querySelector("img").alt = button.querySelector("img").alt;
    dialog.showModal();
  });
}
closeButton.addEventListener("click", () => dialog.close());
dialog.addEventListener("click", (event) => {
  const bounds = dialog.getBoundingClientRect();
  if (
    event.clientX < bounds.left ||
    event.clientX > bounds.right ||
    event.clientY < bounds.top ||
    event.clientY > bounds.bottom
  )
    dialog.close();
});
if (
  "IntersectionObserver" in window &&
  !matchMedia("(prefers-reduced-motion: reduce)").matches
) {
  const observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          entry.target.classList.remove("pending");
          observer.unobserve(entry.target);
        }
      }
    },
    { threshold: 0.08 },
  );
  for (const section of document.querySelectorAll(
    ".capabilities, .local, .download",
  )) {
    section.classList.add("reveal", "pending");
    observer.observe(section);
  }
}
