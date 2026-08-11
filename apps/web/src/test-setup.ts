import "@testing-library/jest-dom";

// jsdom implements no layout, so `scrollIntoView` is simply absent — any component that
// scrolls a ref into view (the conversation thread does on every new message) throws in a
// test although it works in a browser. Stubbed once here rather than in each test.
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = function scrollIntoView() {
    /* no layout in jsdom — nothing to scroll */
  };
}
