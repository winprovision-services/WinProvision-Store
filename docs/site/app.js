'use strict';
const root = document.documentElement;
const themeButton = document.querySelector('#theme');
function setTheme(theme) {
  root.dataset.theme = theme;
  themeButton.setAttribute('aria-label', `Ativar tema ${theme === 'dark' ? 'claro' : 'escuro'}`);
  document.querySelector('meta[name="theme-color"]').content = theme === 'dark' ? '#201e24' : '#f4f2f6';
}
try { setTheme(localStorage.getItem('winprovision-theme') === 'light' ? 'light' : 'dark'); } catch { setTheme('dark'); }
themeButton.addEventListener('click', () => {
  setTheme(root.dataset.theme === 'dark' ? 'light' : 'dark');
  try { localStorage.setItem('winprovision-theme', root.dataset.theme); } catch { /* Navegação com armazenamento indisponível. */ }
});
const screens = {
  descobrir: ['Encontre seu próximo favorito.', 'Destaques, categorias e pesquisa em uma página feita para explorar aplicativos e consultar seus detalhes.', 'inicio-escuro.png', 'Tela Descobrir do WinProvision Store'],
  atualizacoes: ['Um olhar para o que vem a seguir.', 'Consulte as versões disponíveis, filtre as fontes e escolha os aplicativos que deseja atualizar.', 'atualizacoes-escuro.png', 'Tela de atualizações do WinProvision Store'],
  instalados: ['Conheça o que faz parte do seu Windows.', 'Consulte seus programas instalados, pesquise na lista e acesse as opções de gerenciamento.', 'instalados-escuro.png', 'Tela de aplicativos instalados do WinProvision Store'],
  colecoes: ['Seu conjunto. Sua rotina.', 'Agrupe os aplicativos que você usa em coleções. Prepare sua seleção para instalar novamente ou compartilhar por arquivo.', 'colecoes-escuro.png', 'Tela de coleções de aplicativos do WinProvision Store'],
  office: ['O Office com as suas escolhas.', 'Selecione aplicativos, edição, arquitetura e idioma. Configure a instalação online ou prepare a mídia para uso offline.', 'office-escuro.png', 'Tela de configuração do Office no WinProvision Store']
};
const tabs = [...document.querySelectorAll('[data-screen]')];
function selectScreen(tab) {
  const [title, copy, file, alt] = screens[tab.dataset.screen];
  tabs.forEach(item => { item.setAttribute('aria-selected', String(item === tab)); item.tabIndex = item === tab ? 0 : -1; });
  document.querySelector('#screen-panel').setAttribute('aria-labelledby', tab.id);
  document.querySelector('#screen-title').textContent = title;
  document.querySelector('#screen-copy').textContent = copy;
  const img = document.querySelector('#feature-image'); img.src = `assets/screenshots/${file}`; img.alt = alt;
}
tabs.forEach((tab, index) => {
  tab.addEventListener('click', () => selectScreen(tab));
  tab.addEventListener('keydown', event => {
    let next;
    if (event.key === 'ArrowRight') next = (index + 1) % tabs.length;
    if (event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length;
    if (event.key === 'Home') next = 0;
    if (event.key === 'End') next = tabs.length - 1;
    if (next !== undefined) { event.preventDefault(); selectScreen(tabs[next]); tabs[next].focus(); }
  });
});
const commands = {
  search: ['winprovision search Chrome', 'Encontre um aplicativo pelo nome.'],
  install: ['winprovision install --id Google.Chrome', 'Instale pelo identificador completo do pacote.'],
  upgrade: ['winprovision upgrade --all', 'Solicite a atualização de todos os pacotes elegíveis.'],
  export: ['winprovision export --output apps.json', 'Exporte sua lista de aplicativos para um arquivo JSON.']
};
document.querySelectorAll('[data-command]').forEach(button => button.addEventListener('click', () => {
  document.querySelectorAll('[data-command]').forEach(item => item.classList.toggle('selected', item === button));
  const [command, hint] = commands[button.dataset.command];
  document.querySelector('#command').textContent = command; document.querySelector('#command-hint').textContent = hint;
}));
const copyButton = document.querySelector('#copy-command');
copyButton.addEventListener('click', async () => {
  try { await navigator.clipboard.writeText(document.querySelector('#command').textContent); copyButton.textContent = 'Copiado ✓'; }
  catch { copyButton.textContent = 'Selecione o comando'; }
  setTimeout(() => { copyButton.textContent = 'Copiar'; }, 2200);
});
const lightbox = document.querySelector('#lightbox');
document.querySelector('#expand-screen').addEventListener('click', () => {
  const source = document.querySelector('#feature-image'); const target = document.querySelector('#lightbox-image');
  target.src = source.src; target.alt = source.alt; lightbox.showModal(); document.body.classList.add('modal-open');
});
document.querySelector('#close-lightbox').addEventListener('click', () => lightbox.close());
lightbox.addEventListener('close', () => document.body.classList.remove('modal-open'));
lightbox.addEventListener('click', event => { if (event.target === lightbox) lightbox.close(); });
const navLinks = [...document.querySelectorAll('.sidebar nav a')];
if ('IntersectionObserver' in window) {
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) if (entry.isIntersecting) navLinks.forEach(link => {
      const active = link.hash === `#${entry.target.id}`; link.classList.toggle('active', active);
      if (active) link.setAttribute('aria-current', 'location'); else link.removeAttribute('aria-current');
    });
  }, { rootMargin: '-15% 0px -55% 0px' });
  navLinks.forEach(link => observer.observe(document.querySelector(link.hash)));
}
