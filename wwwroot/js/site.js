// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
document.querySelector('#notifyBtn')?.addEventListener('click', () => {
  const toastEl = document.getElementById('deployToast');
  if (toastEl) new mdb.Toast(toastEl).show();
});
