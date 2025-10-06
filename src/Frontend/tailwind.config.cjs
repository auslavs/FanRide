module.exports = {
  content: ["./index.html", "./src/**/*.{fs,js,ts,jsx,tsx}"],
  theme: {
    extend: {}
  },
  plugins: [require("@tailwindcss/forms")]
};
